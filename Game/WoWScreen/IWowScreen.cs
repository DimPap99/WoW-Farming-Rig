using SharedLib;

using System;

namespace Game;

public interface IWowScreen : IRectProvider, IScreenImageProvider, IMinimapImageProvider, IDisposable
{
    // Advances only after a new full-screen image has been copied successfully.
    long ScreenFrameId { get; }

    bool Enabled { get; set; }

    bool MinimapEnabled { get; set; }

    bool EnablePostProcess { get; set; }
    void PostProcess();

    event Action OnChanged;

    void Update();
}
