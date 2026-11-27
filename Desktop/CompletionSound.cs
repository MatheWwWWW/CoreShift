using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Media;

namespace CoreShift.Desktop;

internal static class CompletionSound
{
    private static readonly Lazy<SoundPlayer> Player = new(() =>
    {
        var stream = typeof(CompletionSound).Assembly.GetManifestResourceStream("CoreShift.OperationComplete.wav")
            ?? throw new InvalidOperationException("The CoreShift completion sound is missing.");
        return new SoundPlayer(stream);
    });

    internal static bool Play()
    {
        try
        {
            Player.Value.Play();
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or TimeoutException or Win32Exception)
        {
            Debug.WriteLine(ex);
            return false;
        }
    }
}
