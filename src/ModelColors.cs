using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Media;

namespace CodexUserData
{
    internal static class ModelColors
    {
        private static readonly Dictionary<string,SolidColorBrush> brushes=new Dictionary<string,SolidColorBrush>(StringComparer.Ordinal);
        private static readonly Dictionary<string,SolidColorBrush> aliases=new Dictionary<string,SolidColorBrush>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string,SolidColorBrush> fills=new Dictionary<string,SolidColorBrush>(StringComparer.Ordinal);
        private static readonly Dictionary<string,SolidColorBrush> fillAliases=new Dictionary<string,SolidColorBrush>(StringComparer.OrdinalIgnoreCase);
        // Calibrated against all existing ten-color windows: already saturated
        // greens need less enhancement to avoid collapsing neighboring shades.
        private static readonly double[] fillStrength={.75,.95,.30,1,1};
        internal static Brush For(string model)
        {
            model=String.IsNullOrWhiteSpace(model)?"unknown":model.Trim();SolidColorBrush brush;
            // Chart frames normally hit this lookup directly: version parsing
            // and provider/zero-version normalization happen only on first use.
            if(aliases.TryGetValue(model,out brush))return brush;
            string key=ModelIdentity.Canonical(model);
            if(!brushes.TryGetValue(key,out brush)){brush=Theme.LiveColor(ColorHex(key,Theme.IsLight));brushes.Add(key,brush);}
            if(model.Length<=160)aliases[model]=brush;return brush;
        }
        internal static string ColorHex(string model,bool light)
        {var snapshot=ModelPaletteCatalog.Snapshot;return ModelColorMath.Hex(snapshot.ColorFor(ModelIdentity.Canonical(model),light));}
        internal static Brush FillFor(string model)
        {
            model=String.IsNullOrWhiteSpace(model)?"unknown":model.Trim();SolidColorBrush brush;
            if(fillAliases.TryGetValue(model,out brush))return brush;
            string key=ModelIdentity.Canonical(model);
            if(!fills.TryGetValue(key,out brush)){brush=Theme.LiveColor(ModelColorMath.Hex(FillColor(ModelPaletteCatalog.Snapshot,key,Theme.IsLight)));fills.Add(key,brush);}
            if(model.Length<=160)fillAliases[model]=brush;return brush;
        }
        private static Color FillColor(ModelPaletteSnapshot snapshot,string canonical,bool light)
        {
            int family=ModelIdentity.Family(canonical);
            if(family==-2)return snapshot.ColorFor(canonical,light);
            // Large filled shapes can use the brighter member of the palette
            // in either theme. Text and thin curves retain their readable colors.
            return VividFill(snapshot.ColorFor(canonical,false),family);
        }
        internal static Color VividFill(Color basis,int family)
        {
            var lab=ModelColorMath.Lab(basis);double chroma=Math.Sqrt(lab[1]*lab[1]+lab[2]*lab[2]);if(chroma<.001)return basis;
            double strength=family>=0?fillStrength[family]:.5;
            double L=lab[0]-.06*strength,scale=1+.25*strength+.025*strength/chroma,a=lab[1]*scale,b=lab[2]*scale;Color result;
            if(ModelColorMath.TryLab(L,a,b,out result))return result;
            // Reduce chroma at fixed perceptual hue/lightness instead of clipping
            // RGB channels. This runs only on first use or a palette update.
            double low=0,high=1;for(int i=0;i<18;i++){double middle=(low+high)/2;if(ModelColorMath.TryLab(L,a*middle,b*middle,out result))low=middle;else high=middle;}
            ModelColorMath.TryLab(L,a*low,b*low,out result);return result;
        }
        internal static KeyValuePair<SolidColorBrush,Color>[] Prepare(ModelPaletteSnapshot snapshot)
        {
            bool light=Theme.IsLight;var result=new List<KeyValuePair<SolidColorBrush,Color>>(brushes.Count+fills.Count);
            foreach(var pair in brushes)result.Add(new KeyValuePair<SolidColorBrush,Color>(pair.Value,snapshot.ColorFor(pair.Key,light)));
            foreach(var pair in fills)result.Add(new KeyValuePair<SolidColorBrush,Color>(pair.Value,FillColor(snapshot,pair.Key,light)));
            return result.ToArray();
        }
        internal static void Commit(KeyValuePair<SolidColorBrush,Color>[] prepared)
        {foreach(var pair in prepared)pair.Key.Color=pair.Value;}
        internal static void RefreshTheme(){Commit(Prepare(ModelPaletteCatalog.Snapshot));}
        internal static string Effort(string effort){return String.IsNullOrEmpty(effort)||effort=="unknown"?"未记录":effort;}
        internal static string Money(decimal value,long unpriced,long tokens){return value>0&&value<.01m?"<$0.01":"$"+value.ToString("N2",CultureInfo.InvariantCulture);}
    }
}
