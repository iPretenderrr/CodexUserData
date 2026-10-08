// Brand timing and PCM/lifecycle checks. Nothing here plays the startup sound.
import assert from 'node:assert/strict';
import { createServer } from 'node:http';
import { createReadStream } from 'node:fs';
import { readFile, writeFile, stat, mkdtemp, rm } from 'node:fs/promises';
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { tmpdir } from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = fileURLToPath(new URL('../', import.meta.url));
// Install puppeteer-core locally, or point this environment variable at an
// existing installation. The verifier does not depend on preview tooling.
const require = createRequire(import.meta.url);
const puppeteer = require(process.env.STARTUP_PUPPETEER_MODULE || 'puppeteer-core');
const cues = { cinema: 7.75, layers: 5, matrix: 6.6, prism: 7.85 };
const clip = await readFile(path.join(root, 'assets/startup/chime.wav'));
assert.equal(clip.toString('ascii', 0, 4), 'RIFF');
assert.equal(clip.toString('ascii', 8, 12), 'WAVE');
assert.equal(clip.readUInt16LE(20), 1, 'Uncompressed PCM');
assert.equal(clip.readUInt16LE(22), 1, 'Mono');
assert.equal(clip.readUInt32LE(24), 44100);
assert.equal(clip.readUInt16LE(34), 16);
assert.equal(clip.readUInt32LE(40), clip.length - 44);
const count = (clip.length - 44) / 2;
const duration = count / 44100;
assert.ok(duration >= 2.3 && duration <= 2.6);
let peak = 0;
let power = 0;
let dc = 0;
for (let index = 0; index < count; index++) {
  const value = clip.readInt16LE(44 + index * 2) / 32768;
  peak = Math.max(peak, Math.abs(value)); power += value * value; dc += value;
}
const peakDbFS = 20 * Math.log10(peak);
assert.ok(Math.abs(peakDbFS + 17) < 0.02, 'Restrained peak without clipping');
assert.ok(Math.abs(dc / count) < 0.0001, 'No material DC offset');
assert.equal(clip.readInt16LE(44), 0, 'Soft onset');
for (let index = count - 100; index < count; index++) assert.ok(Math.abs(clip.readInt16LE(44 + index * 2)) < 5, 'Tail settles to silence');

const temporary = await mkdtemp(path.join(tmpdir(), 'codex-startup-brand-check-'));
let browser;
let server;
const reports = [];
try {
  const probeSource = path.join(temporary, 'ChimeProbe.cs');
  const probeExe = path.join(temporary, 'ChimeProbe.exe');
  await writeFile(probeSource, `using System; using System.Media; using System.Reflection;
namespace CodexUserData { internal static class ChimeProbe {
static void Main() {
using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexUserData.Startup.chime.wav")) {
if(stream==null) throw new Exception("Missing chime");
using(var player=new SoundPlayer(stream)) { player.Load(); if(!player.IsLoadCompleted) throw new Exception("PCM load failed"); }
}
var stopped=new StartupChime(); stopped.Stop(); stopped.Stop(); stopped.PlayOnce(); stopped.PlayOnce(); stopped.Dispose(); stopped.Dispose();
var disposed=new StartupChime(); disposed.Dispose(); disposed.PlayOnce(); disposed.Stop(); disposed.Dispose();
Console.WriteLine("PCM load and cancelled lifecycle passed; no playback requested.");
} } }`);
  const compiler = path.join(process.env.WINDIR || 'C:/Windows', 'Microsoft.NET/Framework64/v4.0.30319/csc.exe');
  const compile = spawnSync(compiler, ['/nologo', '/target:exe', '/platform:x64', '/codepage:65001', `/out:${probeExe}`,
    `/resource:${path.join(root, 'assets/startup/chime.wav')},CodexUserData.Startup.chime.wav`,
    path.join(root, 'src/StartupChime.cs'), probeSource], { encoding: 'utf8', windowsHide: true });
  assert.equal(compile.status, 0, compile.stderr || compile.stdout || String(compile.error));
  const lifecycle = spawnSync(probeExe, [], { encoding: 'utf8', windowsHide: true });
  assert.equal(lifecycle.status, 0, lifecycle.stderr || lifecycle.stdout || String(lifecycle.error));

  if (!process.argv.includes('--audio-only')) {
    const files = new Map(['index.html', ...Object.keys(cues).map(style => `${style}.webm`)]
      .map(name => [`/${name}`, path.join(root, 'assets/startup', name)]));
    let corrupt = false;
    server = createServer(async (request, response) => {
      const filename = files.get(new URL(request.url, 'http://127.0.0.1').pathname);
      if (!filename) { response.writeHead(404).end(); return; }
      if (corrupt && filename.endsWith('.webm')) { response.writeHead(200, { 'Content-Type': 'video/webm' }).end('invalid WebM'); return; }
      const { size } = await stat(filename);
      const range = /^bytes=(\d+)-(\d*)$/.exec(request.headers.range || '');
      const start = range ? Number(range[1]) : 0;
      const end = range && range[2] ? Math.min(Number(range[2]), size - 1) : size - 1;
      if (start > end || start >= size) { response.writeHead(416).end(); return; }
      response.writeHead(range ? 206 : 200, { 'Content-Type': filename.endsWith('.webm') ? 'video/webm' : 'text/html; charset=utf-8',
        'Content-Length': end - start + 1, 'Cache-Control': 'no-store', 'Accept-Ranges': 'bytes',
        ...(range ? { 'Content-Range': `bytes ${start}-${end}/${size}` } : {}) });
      createReadStream(filename, { start, end }).pipe(response);
    });
    await new Promise(resolve => server.listen(0, '127.0.0.1', resolve));
    const origin = `http://127.0.0.1:${server.address().port}`;
    browser = await puppeteer.launch({ executablePath: process.env.STARTUP_EDGE_PATH || 'C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe',
      headless: true, userDataDir: path.join(temporary, 'edge'),
      args: ['--no-first-run', '--disable-background-networking', '--disable-component-update', '--disable-sync', '--disable-logging', '--disable-crash-reporter'] });

    async function open(style) {
      const page = await browser.newPage();
      const outside = [];
      const errors = [];
      page.on('request', request => { if (new URL(request.url()).origin !== origin) outside.push(request.url()); });
      page.on('pageerror', error => errors.push(error.message));
      await page.evaluateOnNewDocument(() => {
        const listeners = [];
        window.__messages = [];
        window.__play = () => listeners.forEach(listener => listener({ data: { type: 'play' } }));
        Object.defineProperty(window, 'chrome', { configurable: true, value: { webview: {
          addEventListener(type, listener) { if (type === 'message') listeners.push(listener); },
          postMessage(message) { window.__messages.push({ type: message.type, mediaTime: document.querySelector('video').currentTime }); }
        } } });
      });
      await page.goto(`${origin}/index.html?style=${style}`, { waitUntil: 'domcontentloaded' });
      return { page, outside, errors };
    }
    const wait = (page, type, timeout = 5000) => page.waitForFunction(type => window.__messages.some(message => message.type === type), { timeout }, type);
    async function close(context) {
      assert.deepEqual(context.outside, [], 'No outside requests');
      assert.deepEqual(context.errors, [], 'No unhandled page errors');
      const messages = await context.page.evaluate(() => window.__messages);
      await context.page.close();
      return messages;
    }

    // Full serial playback checks the new message without concurrent decoders.
    for (const [style, cue] of process.argv.includes('--events-only') ? [] : Object.entries(cues)) {
      const context = await open(style);
      await wait(context.page, 'ready');
      await context.page.evaluate(() => { window.__play(); window.__play(); });
      await wait(context.page, 'brand', 12000);
      await wait(context.page, 'ended', 10000);
      await context.page.evaluate(() => { window.__play(); document.querySelector('video').dispatchEvent(new Event('timeupdate')); });
      const messages = await close(context);
      assert.deepEqual(messages.map(message => message.type), ['ready', 'started', 'brand', 'ended']);
      const fired = messages.find(message => message.type === 'brand').mediaTime;
      assert.ok(fired >= cue && fired < cue + 0.35, `${style} cue drift: ${fired - cue}`);
      reports.push({ style, cue, fired, delay: +(fired - cue).toFixed(4) });
    }
    {
      const context = await open('cinema');
      await wait(context.page, 'ready');
      await context.page.evaluate(() => window.__play());
      await wait(context.page, 'started');
      await context.page.evaluate(() => { const video = document.querySelector('video'); video.pause(); video.currentTime = 7.65; });
      await context.page.waitForFunction(() => !document.querySelector('video').seeking);
      // Wait beyond the predicted cue while media time is paused.
      await new Promise(resolve => setTimeout(resolve, 450));
      assert.equal(await context.page.evaluate(() => window.__messages.some(message => message.type === 'brand')), false);
      await context.page.evaluate(() => document.querySelector('video').play());
      await wait(context.page, 'brand');
      await context.page.evaluate(() => { const video = document.querySelector('video'); video.currentTime = 7.60; video.dispatchEvent(new Event('timeupdate')); });
      await context.page.waitForFunction(() => !document.querySelector('video').seeking);
      await new Promise(resolve => setTimeout(resolve, 450));
      await context.page.keyboard.press('Escape');
      const messages = await close(context);
      assert.equal(messages.filter(message => message.type === 'brand').length, 1, 'Seeking/replaying does not duplicate brand');
      assert.equal(messages.filter(message => message.type === 'started').length, 1);
      reports.push({ case: 'paused-media-time-and-backward-seek', passed: true });
    }
    for (const terminal of ['skip', 'error']) {
      const context = await open('matrix');
      await wait(context.page, 'ready');
      await context.page.evaluate(() => window.__play());
      await wait(context.page, 'started');
      await context.page.evaluate(({ terminal, cue }) => {
        const video = document.querySelector('video');
        if (terminal === 'skip') video.dispatchEvent(new MouseEvent('dblclick'));
        else video.dispatchEvent(new Event('error'));
        video.currentTime = cue + 0.2;
        video.dispatchEvent(new Event('timeupdate'));
        video.dispatchEvent(new Event('playing'));
        window.__play();
      }, { terminal, cue: cues.matrix });
      await new Promise(resolve => setTimeout(resolve, 300));
      const messages = await close(context);
      assert.deepEqual(messages.map(message => message.type), ['ready', 'started', terminal]);
      reports.push({ case: `early-${terminal}-suppresses-brand`, passed: true });
    }
    corrupt = true;
    {
      const context = await open('cinema');
      await wait(context.page, 'error');
      assert.deepEqual((await close(context)).map(message => message.type), ['error']);
      reports.push({ case: 'decode-error-suppresses-brand', passed: true });
    }
  }
  console.log(JSON.stringify({ passed: true, audio: { duration, sampleRate: 44100, bits: 16, channels: 1,
    peakDbFS: +peakDbFS.toFixed(3), rmsDbFS: +(20 * Math.log10(Math.sqrt(power / count))).toFixed(3), lifecycle: 'passed without playback' }, cases: reports }));
} finally {
  if (browser) await browser.close();
  if (server) await new Promise(resolve => server.close(resolve));
  if (path.dirname(temporary) === path.resolve(tmpdir()) && path.basename(temporary).startsWith('codex-startup-brand-check-')) {
    await rm(temporary, { recursive: true, force: true, maxRetries: 3, retryDelay: 100 });
  }
}
