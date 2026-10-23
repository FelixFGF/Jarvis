using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Color = System.Windows.Media.Color;
using Brushes = System.Windows.Media.Brushes;
using Point = System.Windows.Point;
using Size = System.Windows.Size;
using Orientation = System.Windows.Controls.Orientation;
using ProgressBar = System.Windows.Controls.ProgressBar;
using Image = System.Windows.Controls.Image;
using Button = System.Windows.Controls.Button;
using ColorConverter = System.Windows.Media.ColorConverter;

namespace Jarvis.Services;

public static class VisualResponseRenderer
{
    private static readonly Color[] Palette = [Color.FromRgb(139,92,246), Color.FromRgb(59,130,246), Color.FromRgb(34,197,94), Color.FromRgb(245,158,11), Color.FromRgb(236,72,153), Color.FromRgb(6,182,212)];
    public static FrameworkElement Render(VisualResponseDocument document, bool german)
    {
        var wrap = new WrapPanel(); foreach (var block in document.Blocks) wrap.Children.Add(Card(block, german));
        return new ScrollViewer { Content = wrap, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, MaxWidth = 760 };
    }
    private static Border Card(VisualBlock b, bool german)
    {
        var body = new StackPanel(); body.Children.Add(new TextBlock { Text = Emoji(b.Type) + "  " + b.Title, FontSize=16, FontWeight=FontWeights.SemiBold, Foreground=Brushes.White, TextWrapping=TextWrapping.Wrap });
        if (!string.IsNullOrWhiteSpace(b.Subtitle)) body.Children.Add(new TextBlock { Text=b.Subtitle, Foreground=new SolidColorBrush(Color.FromRgb(166,166,184)), TextWrapping=TextWrapping.Wrap, Margin=new Thickness(0,3,0,10) });
        body.Children.Add(b.Type.ToLowerInvariant() switch
        {
            "bar" or "stackedbar" => Bars(b, b.Type.Equals("stackedBar",StringComparison.OrdinalIgnoreCase)),
            "line" or "area" => Lines(b, b.Type.Equals("area",StringComparison.OrdinalIgnoreCase)),
            "pie" or "donut" => Pie(b, b.Type.Equals("donut",StringComparison.OrdinalIgnoreCase)),
            "kpi" => Kpis(b), "table" or "comparison" => Table(b), "progress" => Progress(b),
            "gallery" => Gallery(b, german), "timeline" => Timeline(b), "ranking" => Ranking(b), _ => new TextBlock { Text="Unsupported visual" }
        });
        return new Border { Width=350, MaxWidth=720, Margin=new Thickness(0,0,10,10), Padding=new Thickness(14), CornerRadius=new CornerRadius(12), Background=new SolidColorBrush(Color.FromRgb(24,25,34)), BorderBrush=new SolidColorBrush(Color.FromRgb(67,58,103)), BorderThickness=new Thickness(1), Child=body };
    }
    private static FrameworkElement Bars(VisualBlock b, bool stacked)
    {
        var max=Math.Max(1,b.Series.SelectMany(s=>s.Values).Select(Math.Abs).DefaultIfEmpty(1).Max()); var panel=new StackPanel();
        for(int i=0;i<b.Series.Max(s=>s.Values.Count);i++) { var row=new StackPanel{Margin=new Thickness(0,3,0,3)}; row.Children.Add(new TextBlock{Text=i<b.Categories.Count?b.Categories[i]:$"{i+1}",Foreground=Brushes.LightGray}); var bars=new StackPanel{Orientation=Orientation.Horizontal}; foreach(var (s,si) in b.Series.Select((s,i)=>(s,i))){var v=s.Values[i]; bars.Children.Add(new Border{Width=Math.Max(2,Math.Abs(v)/max*(stacked?260d/b.Series.Count:260)),Height=16,Background=new SolidColorBrush(SeriesColor(s,si)),ToolTip=$"{s.Name}: {v:N2}",Margin=new Thickness(0,1,3,1)});} row.Children.Add(bars); panel.Children.Add(row);} return panel;
    }
    private static FrameworkElement Lines(VisualBlock b,bool area)
    {
        var canvas=new Canvas{Height=190,Width=310,Background=new SolidColorBrush(Color.FromRgb(19,20,28))}; var values=b.Series.SelectMany(s=>s.Values).ToArray(); var min=Math.Min(0,values.Min()); var max=Math.Max(1,values.Max());
        foreach(var (s,si) in b.Series.Select((s,i)=>(s,i))){var points=new PointCollection();for(int i=0;i<s.Values.Count;i++)points.Add(new Point(s.Values.Count==1?155:i*300d/(s.Values.Count-1),180-(s.Values[i]-min)/Math.Max(.0001,max-min)*165));var pc=SeriesColor(s,si);if(area){var fill=new PointCollection(points);fill.Add(new Point(points[^1].X,180));fill.Add(new Point(points[0].X,180));canvas.Children.Add(new Polygon{Points=fill,Fill=new SolidColorBrush(Color.FromArgb(45,pc.R,pc.G,pc.B))});}canvas.Children.Add(new Polyline{Points=points,Stroke=new SolidColorBrush(pc),StrokeThickness=2.5,ToolTip=s.Name});}return canvas;
    }
    private static FrameworkElement Pie(VisualBlock b,bool donut)
    {
        var canvas=new Canvas{Width=310,Height=210};var vals=b.Series[0].Values.Select(v=>Math.Max(0,v)).ToArray();var total=vals.Sum();double start=-90;for(int i=0;i<vals.Length;i++){var sweep=total<=0?0:vals[i]/total*360;var path=new Path{Fill=new SolidColorBrush(Palette[i%Palette.Length]),Data=Arc(105,105,88,start,sweep,donut?48:0),ToolTip=$"{(i<b.Categories.Count?b.Categories[i]:i+1)}: {vals[i]:N2}"};canvas.Children.Add(path);start+=sweep;}return canvas;
    }
    private static Geometry Arc(double cx,double cy,double r,double start,double sweep,double inner){if(sweep>=359.999){var g=new EllipseGeometry(new Point(cx,cy),r,r);return inner>0?new CombinedGeometry(GeometryCombineMode.Exclude,g,new EllipseGeometry(new Point(cx,cy),inner,inner)):g;}Point P(double radius,double a)=>new(cx+radius*Math.Cos(a*Math.PI/180),cy+radius*Math.Sin(a*Math.PI/180));var fig=new PathFigure{StartPoint=inner>0?P(inner,start):new Point(cx,cy),IsClosed=true};if(inner>0)fig.Segments.Add(new LineSegment(P(r,start),true));else fig.Segments.Add(new LineSegment(P(r,start),true));fig.Segments.Add(new ArcSegment(P(r,start+sweep),new Size(r,r),0,sweep>180,SweepDirection.Clockwise,true));if(inner>0){fig.Segments.Add(new LineSegment(P(inner,start+sweep),true));fig.Segments.Add(new ArcSegment(P(inner,start),new Size(inner,inner),0,sweep>180,SweepDirection.Counterclockwise,true));}return new PathGeometry([fig]);}
    private static FrameworkElement Kpis(VisualBlock b){var w=new WrapPanel();foreach(var m in b.Metrics)w.Children.Add(new Border{Width=145,Margin=new Thickness(3),Padding=new Thickness(10),CornerRadius=new CornerRadius(8),Background=new SolidColorBrush(Color.FromRgb(34,34,48)),Child=new StackPanel{Children={new TextBlock{Text=m.Label,Foreground=Brushes.LightGray,TextWrapping=TextWrapping.Wrap},new TextBlock{Text=$"{m.Value:N2} {m.Unit}".Trim(),FontSize=21,FontWeight=FontWeights.Bold,Foreground=new SolidColorBrush(Palette[0])},new TextBlock{Text=m.Change is null?"":$"{(m.Change>=0?"📈":"📉")} {m.Change:+0.##;-0.##;0}%",Foreground=Brushes.LightGray}}}});return w;}
    private static FrameworkElement Table(VisualBlock b){var g=new Grid();for(int c=0;c<b.Columns.Count;c++)g.ColumnDefinitions.Add(new ColumnDefinition{Width=new GridLength(1,GridUnitType.Star)});void Row(IReadOnlyList<string> cells,int r,bool head){g.RowDefinitions.Add(new RowDefinition{Height=GridLength.Auto});for(int c=0;c<cells.Count;c++){var x=new Border{Padding=new Thickness(6),BorderBrush=new SolidColorBrush(Color.FromRgb(55,56,70)),BorderThickness=new Thickness(0,0,1,1),Background=head?new SolidColorBrush(Color.FromRgb(45,39,67)):Brushes.Transparent,Child=new TextBlock{Text=cells[c],TextWrapping=TextWrapping.Wrap,Foreground=Brushes.White,FontWeight=head?FontWeights.SemiBold:FontWeights.Normal}};Grid.SetRow(x,r);Grid.SetColumn(x,c);g.Children.Add(x);}}Row(b.Columns,0,true);for(int i=0;i<b.Rows.Count;i++)Row(b.Rows[i],i+1,false);return new ScrollViewer{Content=g,HorizontalScrollBarVisibility=ScrollBarVisibility.Auto};}
    private static FrameworkElement Progress(VisualBlock b){var p=new ProgressBar{Minimum=0,Maximum=b.Maximum!.Value,Value=b.Value!.Value,Height=18,Foreground=new SolidColorBrush(Palette[0])};var s=new StackPanel();s.Children.Add(p);s.Children.Add(new TextBlock{Text=$"{b.Value:N1} / {b.Maximum:N1} {b.Unit}".Trim(),Foreground=Brushes.LightGray,Margin=new Thickness(0,4,0,0)});return s;}
    private static FrameworkElement Gallery(VisualBlock b,bool german){var w=new WrapPanel();foreach(var i in b.Images){var image=new Image{Width=135,Height=90,Stretch=Stretch.UniformToFill};var bmp=new BitmapImage();bmp.BeginInit();bmp.UriSource=new Uri(i.ThumbnailUrl);bmp.DecodePixelWidth=320;bmp.CacheOption=BitmapCacheOption.OnDemand;bmp.EndInit();image.Source=bmp;var button=new Button{Content=german?"Quelle":"Source",Tag=i.SourceUrl,Padding=new Thickness(5)};button.Click+=(_,_)=>Process.Start(new ProcessStartInfo((string)button.Tag){UseShellExecute=true});w.Children.Add(new Border{Width=155,Margin=new Thickness(3),Padding=new Thickness(7),Background=new SolidColorBrush(Color.FromRgb(34,34,47)),CornerRadius=new CornerRadius(8),Child=new StackPanel{Children={image,new TextBlock{Text=i.Title,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.White,Margin=new Thickness(0,4,0,2)},new TextBlock{Text=$"{i.Author} · {i.License}".Trim(' ','·'),FontSize=9,TextWrapping=TextWrapping.Wrap,Foreground=Brushes.LightGray},button}}});}return w;}
    private static FrameworkElement Timeline(VisualBlock b){var s=new StackPanel();foreach(var (x,i) in b.Steps.Select((x,i)=>(x,i)))s.Children.Add(new Border{BorderBrush=new SolidColorBrush(Palette[i%Palette.Length]),BorderThickness=new Thickness(3,0,0,0),Padding=new Thickness(10,5,5,5),Margin=new Thickness(0,2,0,2),Child=new TextBlock{Text=$"{StatusEmoji(x.Status)} {x.Label}\n{x.Detail}".Trim(),TextWrapping=TextWrapping.Wrap,Foreground=Brushes.White}});return s;}
    private static FrameworkElement Ranking(VisualBlock b)=>Bars(b,false);
    private static string Emoji(string type)=>type.ToLowerInvariant() switch{"kpi"=>"📊","gallery"=>"🎨","timeline"=>"🛠️","progress"=>"⚡","comparison"=>"🔍",_=>"📈"};
    private static string StatusEmoji(string? status)=>status?.ToLowerInvariant() switch{"complete" or "completed"=>"✅","failed"=>"❌","active"=>"⚡",_=>"•"};
    private static Color SeriesColor(VisualSeries series,int index){try{if(!string.IsNullOrWhiteSpace(series.Color)&&ColorConverter.ConvertFromString(series.Color) is Color color)return color;}catch(FormatException){}return Palette[index%Palette.Length];}
}
