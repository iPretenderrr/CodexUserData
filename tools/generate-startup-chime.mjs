// Original additive-synthesis cue; no recordings or third-party sample assets.
import { writeFile } from 'node:fs/promises';
import { fileURLToPath } from 'node:url';

const sampleRate = 44100;
const duration = 2.45;
const count = Math.round(sampleRate * duration);
const samples = new Float64Array(count);
const tau = Math.PI * 2;
const smooth = value => { const x = Math.max(0, Math.min(1, value)); return x * x * (3 - 2 * x); };
// A warm D6/9 voicing: a quiet body followed by two rising, overlapping sparks.
// The stagger, envelopes, intervals and partials are authored here from scratch.
const notes = [
  { hz: 146.832384, start: 0, weight: 0.42, decay: 0.98 },
  { hz: 220, start: 0.018, weight: 0.40, decay: 1.10 },
  { hz: 293.664768, start: 0.042, weight: 0.53, decay: 1.01 },
  { hz: 369.994423, start: 0.095, weight: 0.39, decay: 0.92 },
  { hz: 493.883301, start: 0.21, weight: 0.30, decay: 0.80 },
  { hz: 659.255114, start: 0.39, weight: 0.19, decay: 0.67 }
];
for (let index = 0; index < count; index++) {
  const time = index / sampleRate;
  let value = 0;
  for (let noteIndex = 0; noteIndex < notes.length; noteIndex++) {
    const note = notes[noteIndex];
    const age = time - note.start;
    if (age < 0) continue;
    const attack = smooth(age / (noteIndex < 3 ? 0.072 : 0.052));
    const envelope = attack * Math.exp(-age / note.decay) * (1 - smooth((time - 1.79) / (duration - 1.79)));
    const vibrato = 0.0011 * Math.sin(tau * 0.63 * age);
    const phase = tau * note.hz * age + vibrato;
    const tone = Math.sin(phase) + 0.16 * Math.sin(phase * 2 + 0.3) * Math.exp(-age / 0.46)
      + 0.035 * Math.sin(phase * 3 + 0.6) * Math.exp(-age / 0.28);
    value += note.weight * envelope * tone;
    // A low, fixed reflection gives space without a large reverb tail.
    if (age > 0.083) value += note.weight * envelope * Math.sin(tau * note.hz * (age - 0.083)) * 0.065;
  }
  samples[index] = value;
}
const peak = samples.reduce((maximum, value) => Math.max(maximum, Math.abs(value)), 0);
// Peak -17 dBFS is restrained even at high system volume; no runtime gain/filter.
const gain = Math.pow(10, -17 / 20) / peak;
const pcm = Buffer.alloc(count * 2);
for (let index = 0; index < count; index++) pcm.writeInt16LE(Math.round(samples[index] * gain * 32767), index * 2);
const header = Buffer.alloc(44);
header.write('RIFF', 0); header.writeUInt32LE(36 + pcm.length, 4); header.write('WAVE', 8);
header.write('fmt ', 12); header.writeUInt32LE(16, 16); header.writeUInt16LE(1, 20);
header.writeUInt16LE(1, 22); header.writeUInt32LE(sampleRate, 24); header.writeUInt32LE(sampleRate * 2, 28);
header.writeUInt16LE(2, 32); header.writeUInt16LE(16, 34); header.write('data', 36); header.writeUInt32LE(pcm.length, 40);
const destination = fileURLToPath(new URL('../assets/startup/chime.wav', import.meta.url));
await writeFile(destination, Buffer.concat([header, pcm]));
console.log(JSON.stringify({ destination, duration, sampleRate, channels: 1, bits: 16, peakDbFS: -17 }));
