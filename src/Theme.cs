using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Markup;

namespace CodexUserData
{
    internal static partial class Theme
    {
        internal static readonly Dictionary<string,string> MetricLabels=new Dictionary<string,string>{{"tokens","真实消耗 Tokens"},{"requests","请求 / 用量记录"},{"cacheRate","缓存命中率"},{"cost","估算费用 · USD"},{"input","未缓存输入"},{"output","输出 Tokens"},{"cacheRead","缓存读取"},{"reasoning","推理 Tokens"},{"sessions","活跃会话"}};
        internal static Brush B(string color){var b=(SolidColorBrush)new BrushConverter().ConvertFromString(color);b.Freeze();return b;}
        internal static TextBlock Text(string text,double size,Brush color){return new TextBlock{Text=text,FontSize=size,Foreground=color,VerticalAlignment=VerticalAlignment.Center};}
        internal static ControlTemplate RoundTemplate(Type type,int radius)
        {
            var border=new FrameworkElementFactory(typeof(Border));border.Name="Shell";
            border.SetValue(Border.CornerRadiusProperty,new CornerRadius(radius));
            border.SetBinding(Border.BackgroundProperty,new Binding("Background"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
            border.SetBinding(Border.BorderBrushProperty,new Binding("BorderBrush"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
            border.SetBinding(Border.BorderThicknessProperty,new Binding("BorderThickness"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
            border.SetBinding(Border.PaddingProperty,new Binding("Padding"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
            // Keep centered buttons as the default, while allowing navigation and list-style
            // buttons to request their own alignment without maintaining a second template.
            var presenter=new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetBinding(FrameworkElement.HorizontalAlignmentProperty,new Binding("HorizontalContentAlignment"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});
            presenter.SetBinding(FrameworkElement.VerticalAlignmentProperty,new Binding("VerticalContentAlignment"){RelativeSource=new RelativeSource(RelativeSourceMode.TemplatedParent)});border.AppendChild(presenter);
            var template=new ControlTemplate(type){VisualTree=border};
            var hover=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("ThemeHover"),"Shell"));template.Triggers.Add(hover);
            var focus=new Trigger{Property=UIElement.IsKeyboardFocusedProperty,Value=true};focus.Setters.Add(new Setter(Border.BorderBrushProperty,new DynamicResourceExtension("ThemeAccent"),"Shell"));focus.Setters.Add(new Setter(Border.BorderThicknessProperty,new Thickness(1),"Shell"));template.Triggers.Add(focus);
            return template;
        }
        internal static Button Button(string content,string name,double width)
        {
            var b=new Button{Content=content,MinWidth=width,Height=29,Padding=new Thickness(7,3,7,3),Background=Brushes.Transparent,BorderBrush=Line,BorderThickness=new Thickness(0),Foreground=Muted,Cursor=Cursors.Hand,FontSize=13,Template=RoundTemplate(typeof(Button),7)};
            b.Resources["ThemeHover"]=Hover;b.Resources["ThemeAccent"]=Accent;b.ToolTip=name;AutomationProperties.SetName(b,name);return b;
        }
        internal static Button ToolbarButton(string icon,string name)
        {
            // One template and one fixed icon viewport prevent font glyphs from changing visual weight.
            var button=Button("",name,32);button.Width=32;button.Height=32;button.Padding=new Thickness(0);
            button.Margin=new Thickness(2,0,2,0);button.Background=Surface;button.BorderThickness=new Thickness(1);
            button.FocusVisualStyle=null;var template=RoundTemplate(typeof(Button),8);
            var hover=new Trigger{Property=UIElement.IsMouseOverProperty,Value=true};hover.Setters.Add(new Setter(Border.BorderBrushProperty,new DynamicResourceExtension("ThemeAccent"),"Shell"));template.Triggers.Add(hover);
            var selected=new Trigger{Property=FrameworkElement.TagProperty,Value="selected"};selected.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("ThemeHover"),"Shell"));selected.Setters.Add(new Setter(Border.BorderBrushProperty,new DynamicResourceExtension("ThemeAccent"),"Shell"));template.Triggers.Add(selected);
            var pressed=new Trigger{Property=ButtonBase.IsPressedProperty,Value=true};pressed.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("ThemeHover"),"Shell"));template.Triggers.Add(pressed);
            button.Template=template;button.Content=ToolbarIcon(icon);return button;
        }
        internal static FrameworkElement ToolbarIcon(string name)
        {
            string data;
            switch(name)
            {
                case "chart":data="M 4,4 L 20,4 L 20,20 L 4,20 Z M 8,16 L 8,12 M 12,16 L 12,8 M 16,16 L 16,10";break;
                case "settings":data="M 11.02,4.56 L 13.17,3.08 L 15.44,3.69 L 16.57,6.05 L 17.95,7.43 L 20.31,8.56 L 20.92,10.83 L 19.44,12.98 L 18.93,14.87 L 19.14,17.48 L 17.48,19.14 L 14.87,18.93 L 12.98,19.44 L 10.83,20.92 L 8.56,20.31 L 7.43,17.95 L 6.05,16.57 L 3.69,15.44 L 3.08,13.17 L 4.56,11.02 L 5.07,9.13 L 4.86,6.52 L 6.52,4.86 L 9.13,5.07 Z M 15,12 A 3,3 0 1 1 9,12 A 3,3 0 1 1 15,12";break;
                case "bubble":data="M 20,12 A 8,8 0 1 1 4,12 A 8,8 0 1 1 20,12 M 9,12 L 15,12";break;
                case "maximize":data="M 5,5 L 19,5 L 19,19 L 5,19 Z";break;
                case "restore":data="M 8,4 L 20,4 L 20,16 M 4,8 L 16,8 L 16,20 L 4,20 Z";break;
                case "minimize":data="M 5,12 L 19,12";break;
                case "pin":data="M 8,3 L 16,3 M 9,3 L 9,9 L 6,13 L 6,15 L 18,15 L 18,13 L 15,9 L 15,3 M 12,15 L 12,21";break;
                case "expand":data="M 6,9 L 12,15 L 18,9";break;
                case "collapse":data="M 6,15 L 12,9 L 18,15";break;
                default:data="M 6,6 L 18,18 M 18,6 L 6,18";break;
            }
            var geometry=Geometry.Parse(data);geometry.Freeze();
            var path=new System.Windows.Shapes.Path{Data=geometry,StrokeThickness=1.8,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round};
            path.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,new Binding("Foreground"){RelativeSource=new RelativeSource(RelativeSourceMode.FindAncestor,typeof(Button),1)});
            var canvas=new Canvas{Width=24,Height=24};canvas.Children.Add(path);
            return new Viewbox{Width=18,Height=18,Child=canvas,IsHitTestVisible=false};
        }
        internal static FrameworkElement Icon(string name,Brush color)
        {
            string data=name=="refresh"?"M 13,5 A 5.5,5.5 0 1 0 13.2,10 M 13,1 L 13,5 L 9,5":name=="expand"?"M 3,3 L 8,8 L 13,3 M 3,8 L 8,13 L 13,8":"M 3,8 L 8,3 L 13,8 M 3,13 L 8,8 L 13,13";
            var geometry=Geometry.Parse(data);geometry.Freeze();return new System.Windows.Shapes.Path{Data=geometry,Stroke=color,StrokeThickness=1.7,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round,Width=16,Height=16,Stretch=Stretch.Uniform};
        }
        internal static ContextMenu Menu()
        {
            var m=new ContextMenu{Background=Surface,Foreground=Ink,BorderBrush=Line,BorderThickness=new Thickness(1),Padding=new Thickness(5),FontFamily=new FontFamily("Segoe UI, Microsoft YaHei UI"),FontSize=12,HasDropShadow=false};
            // Replace the menu container too: its system template otherwise paints a white icon gutter.
            m.Template=(ControlTemplate)XamlReader.Parse(@"<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' TargetType='ContextMenu'><Border Background='{TemplateBinding Background}' BorderBrush='{TemplateBinding BorderBrush}' BorderThickness='{TemplateBinding BorderThickness}' CornerRadius='9' Padding='{TemplateBinding Padding}'><ScrollViewer CanContentScroll='True' VerticalScrollBarVisibility='Auto' HorizontalScrollBarVisibility='Disabled'><ItemsPresenter/></ScrollViewer></Border></ControlTemplate>");
            InstallStyles(m);
            m.Resources["ThemeInk"]=Ink;var style=new Style(typeof(MenuItem));style.Setters.Add(new Setter(Control.ForegroundProperty,new DynamicResourceExtension("ThemeInk")));style.Setters.Add(new Setter(Control.BackgroundProperty,Brushes.Transparent));
            var border=new FrameworkElementFactory(typeof(Border));border.Name="ItemShell";border.SetValue(Border.PaddingProperty,new Thickness(11,8,11,8));border.SetValue(Border.CornerRadiusProperty,new CornerRadius(6));border.SetValue(Border.BackgroundProperty,Brushes.Transparent);
            var cp=new FrameworkElementFactory(typeof(ContentPresenter));cp.SetValue(ContentPresenter.ContentSourceProperty,"Header");border.AppendChild(cp);
            var t=new ControlTemplate(typeof(MenuItem)){VisualTree=border};var tr=new Trigger{Property=MenuItem.IsHighlightedProperty,Value=true};tr.Setters.Add(new Setter(Border.BackgroundProperty,new DynamicResourceExtension("ThemeHover"),"ItemShell"));t.Triggers.Add(tr);
            style.Setters.Add(new Setter(Control.TemplateProperty,t));m.ItemContainerStyle=style;return m;
        }
        internal static TextBox Input(string text){return new TextBox{Text=text,Background=Surface,Foreground=Ink,BorderBrush=Line,BorderThickness=new Thickness(1),Padding=new Thickness(8,6,8,6),FontSize=12,CaretBrush=Accent};}
        internal static void InstallStyles(FrameworkElement target)
        {
            var resources=(ResourceDictionary)XamlReader.Parse(@"
<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>
 <ControlTemplate x:Key='EmptyRepeat' TargetType='RepeatButton'><Border Background='Transparent'/></ControlTemplate>
 <Style TargetType='ScrollBar'>
  <Setter Property='Width' Value='9'/><Setter Property='MinWidth' Value='9'/><Setter Property='Background' Value='Transparent'/>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='ScrollBar'>
   <Border Background='Transparent' Padding='2,1'><Track x:Name='PART_Track' Orientation='Vertical' IsDirectionReversed='True' Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}' Value='{TemplateBinding Value}' ViewportSize='{TemplateBinding ViewportSize}'>
    <Track.DecreaseRepeatButton><RepeatButton Command='ScrollBar.PageUpCommand' Template='{StaticResource EmptyRepeat}'/></Track.DecreaseRepeatButton>
    <Track.Thumb><Thumb MinHeight='22'><Thumb.Template><ControlTemplate TargetType='Thumb'><Border Background='{DynamicResource ThemeScroll}' CornerRadius='3'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
    <Track.IncreaseRepeatButton><RepeatButton Command='ScrollBar.PageDownCommand' Template='{StaticResource EmptyRepeat}'/></Track.IncreaseRepeatButton>
   </Track></Border>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='CheckBox'>
  <Setter Property='Template'><Setter.Value><ControlTemplate TargetType='CheckBox'>
   <StackPanel Orientation='Horizontal'><Border x:Name='Box' Width='14' Height='14' CornerRadius='4' Background='{DynamicResource ThemeSurface}' BorderBrush='{DynamicResource ThemeScroll}' BorderThickness='1' Margin='0,0,6,0'><TextBlock x:Name='Mark' Text='✓' FontSize='11' Foreground='{DynamicResource ThemeOnAccent}' Visibility='Collapsed' HorizontalAlignment='Center' VerticalAlignment='Center'/></Border><ContentPresenter VerticalAlignment='Center'/></StackPanel>
   <ControlTemplate.Triggers><Trigger Property='IsChecked' Value='True'><Setter TargetName='Box' Property='Background' Value='{DynamicResource ThemeAccent}'/><Setter TargetName='Mark' Property='Visibility' Value='Visible'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Box' Property='BorderBrush' Value='{DynamicResource ThemeAccent}'/></Trigger></ControlTemplate.Triggers>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
 <Style TargetType='Slider'>
  <Setter Property='Height' Value='24'/><Setter Property='Template'><Setter.Value><ControlTemplate TargetType='Slider'>
   <Grid><Border Height='4' Background='{DynamicResource ThemeLine}' CornerRadius='2' VerticalAlignment='Center'/><Track x:Name='PART_Track' Orientation='Horizontal' Minimum='{TemplateBinding Minimum}' Maximum='{TemplateBinding Maximum}' Value='{TemplateBinding Value}'>
    <Track.DecreaseRepeatButton><RepeatButton Command='Slider.DecreaseLarge' Template='{StaticResource EmptyRepeat}'/></Track.DecreaseRepeatButton>
    <Track.Thumb><Thumb Width='16' Height='16'><Thumb.Template><ControlTemplate TargetType='Thumb'><Border Background='{DynamicResource ThemeAccent}' CornerRadius='8'/></ControlTemplate></Thumb.Template></Thumb></Track.Thumb>
    <Track.IncreaseRepeatButton><RepeatButton Command='Slider.IncreaseLarge' Template='{StaticResource EmptyRepeat}'/></Track.IncreaseRepeatButton>
   </Track></Grid>
  </ControlTemplate></Setter.Value></Setter>
 </Style>
</ResourceDictionary>");
            resources["ThemeSurface"]=Surface;resources["ThemeAccent"]=Accent;resources["ThemeHover"]=Hover;resources["ThemeOnAccent"]=OnAccent;resources["ThemeLine"]=Line;resources["ThemeScroll"]=Scroll;
            target.Resources.MergedDictionaries.Add(resources);
        }
    }
    internal sealed class ChoiceButton : Button
    {
        private readonly Dictionary<string,string> options;
        private readonly TextBlock label;
        internal string SelectedKey {get;private set;}
        internal event Action<string> Changed;
        internal ChoiceButton(Dictionary<string,string> choices,string name)
        {
            options=choices;Background=Theme.Surface;Foreground=Theme.Ink;BorderBrush=Theme.Line;BorderThickness=new Thickness(1);Height=28;Padding=new Thickness(9,3,9,3);FontSize=11;Cursor=Cursors.Hand;Template=Theme.RoundTemplate(typeof(Button),8);
            var row=new DockPanel();var arrow=Theme.Text("⌄",13,Theme.Muted);arrow.Margin=new Thickness(9,0,0,0);DockPanel.SetDock(arrow,Dock.Right);row.Children.Add(arrow);
            label=Theme.Text("",11,Theme.Ink);label.TextTrimming=TextTrimming.CharacterEllipsis;row.Children.Add(label);Content=row;AutomationProperties.SetName(this,name);ToolTip=name;
            Click+=delegate
            {
                var menu=Theme.Menu();menu.MinWidth=Math.Max(ActualWidth,145);menu.Placement=PlacementMode.Bottom;menu.PlacementTarget=this;menu.VerticalOffset=5;
                foreach(var entry in options){string key=entry.Key;var item=new MenuItem{Header=(SelectedKey==key?"✓  ":"    ")+entry.Value};AutomationProperties.SetName(item,entry.Value);item.Click+=delegate{Select(key);if(Changed!=null)Changed(key);};menu.Items.Add(item);}
                ContextMenu=menu;menu.IsOpen=true;
            };
        }
        internal void Select(string key){SelectedKey=options.ContainsKey(key)?key:new List<string>(options.Keys)[0];label.Text=options[SelectedKey];AutomationProperties.SetHelpText(this,label.Text);}
    }
}
