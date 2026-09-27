using Game;
using SharedLib;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using System;

namespace ForeverVisualReader;

internal sealed class SyntheticWowScreen(Image<Bgra32> image) : IWowScreen
{
    public long ScreenFrameId => 1;
    public bool Enabled { get; set; }
    public bool MinimapEnabled { get; set; }
    public bool EnablePostProcess { get; set; }
    public event Action OnChanged { add { } remove { } }
    public Image<Bgra32> ScreenImage => image;
    public Image<Bgra32> MiniMapImage => image;
    public Rectangle ScreenRect => new(0, 0, image.Width, image.Height);
    public Rectangle MiniMapRect => ScreenRect;
    public MinimapSettings MinimapSettings => default;
    public void GetPosition(ref Point point) => point = default;
    public void GetRectangle(out Rectangle rect) => rect = ScreenRect;
    public void Update() { }
    public void PostProcess() { }
    public void Dispose() => image.Dispose();
}
