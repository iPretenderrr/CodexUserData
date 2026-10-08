using System;
using System.IO;
using System.Media;
using System.Reflection;
using System.Threading;

namespace CodexUserData
{
    // Small, embedded PCM audio. Loading and starting the optional sound happen
    // off the UI thread; a missing decoder/device must never delay the widget.
    internal sealed class StartupChime : IDisposable
    {
        private readonly object gate = new object();
        private bool requested, stopped;
        private SoundPlayer player;
        private Stream resource;

        internal StartupChime() { }

        internal void PlayOnce()
        {
            lock (gate)
            {
                if (requested || stopped) return;
                requested = true;
            }
            try { ThreadPool.QueueUserWorkItem(PlayWorker); }
            catch (Exception) { Stop(); }
        }

        private void PlayWorker(object state)
        {
            SoundPlayer loaded = null;
            Stream clip = null;
            try
            {
                lock (gate) { if (stopped) return; }
                clip = Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexUserData.Startup.chime.wav");
                // SoundPlayer's empty source can play the system beep. Refuse
                // missing resources instead of substituting a surprising sound.
                if (clip == null) return;
                loaded = new SoundPlayer(clip);
                loaded.Load();
                lock (gate)
                {
                    if (stopped) return;
                    player = loaded; resource = clip;
                    loaded = null; clip = null;
                    // Load completed on this worker; Play is asynchronous and
                    // the lock only orders the native start against a late skip.
                    player.Play();
                }
            }
            catch (Exception) { Stop(); }
            finally
            {
                if (loaded != null) try { loaded.Dispose(); } catch (Exception) { }
                if (clip != null) try { clip.Dispose(); } catch (Exception) { }
            }
        }

        internal void Stop()
        {
            SoundPlayer current;
            Stream clip;
            lock (gate)
            {
                stopped = true;
                current = player; clip = resource;
                player = null; resource = null;
            }
            // Cancellation also covers a queued/load-in-progress worker, which
            // releases its own local resources before it can start playback.
            if (current != null)
            {
                try { current.Stop(); } catch (Exception) { }
                try { current.Dispose(); } catch (Exception) { }
            }
            if (clip != null) try { clip.Dispose(); } catch (Exception) { }
        }

        public void Dispose() { Stop(); }
    }
}
