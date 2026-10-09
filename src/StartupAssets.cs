using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Windows.Media.Imaging;

namespace CodexUserData
{
    // Own streams until WebView has stopped reading them. The native poster is
    // decoded once at display resolution, independent of browser initialization.
    internal sealed class StartupAssets : IDisposable
    {
        private readonly List<Stream> streams=new List<Stream>();

        internal Stream Open(string name)
        {
            var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexUserData.Startup."+name);
            if(stream!=null)streams.Add(stream);
            return stream;
        }

        internal static BitmapSource Poster(string style)
        {
            using(var stream=Assembly.GetExecutingAssembly().GetManifestResourceStream("CodexUserData.Startup."+style+".png"))
            {
                if(stream==null)throw new IOException("Missing startup poster.");
                var image=new BitmapImage();image.BeginInit();image.CacheOption=BitmapCacheOption.OnLoad;
                image.DecodePixelWidth=920;image.StreamSource=stream;image.EndInit();image.Freeze();return image;
            }
        }

        public void Dispose()
        {foreach(var stream in streams)stream.Dispose();streams.Clear();}
    }
}
