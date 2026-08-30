namespace OpenCode.Sdk.Tests;

using OpenTui.Native;

public class OpenTuiTests
{
    [Fact]
    public void CanInitializeAndDestroyNativeOpenTuiRenderer()
    {
        // Width: 80, Height: 24, bufferedOutputKind: 0, remoteMode: 0, feedPtr: null
        uint rendererHandle = OpenTuiNative.CreateRenderer(80, 24, 0, 0, IntPtr.Zero);
        Assert.True(rendererHandle > 0, "Renderer handle should be greater than zero.");

        try
        {
            OpenTuiNative.SetTitle(rendererHandle, "opencode-dotnet native tui test");

            uint bufferHandle = OpenTuiNative.GetCurrentBuffer(rendererHandle);
            Assert.True(bufferHandle > 0, "Buffer handle should be greater than zero.");

            OpenTuiNative.DrawText(bufferHandle, "Hello OpenTui from .NET 10!", 2, 2, NativeRgba.Cyan);
        }
        finally
        {
            OpenTuiNative.DestroyRenderer(rendererHandle, flushInput: false);
        }
    }
}
