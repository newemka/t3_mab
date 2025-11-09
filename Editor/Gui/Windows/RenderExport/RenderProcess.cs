#nullable enable
using System.IO;
using T3.Core.Animation;
using T3.Core.Audio;
using T3.Core.DataTypes;
using T3.Core.DataTypes.Vector;
using T3.Core.Utils;
using T3.Editor.Gui.UiHelpers;
using T3.Editor.Gui.Windows.Output;
using T3.Editor.Gui.Windows.RenderExport.MF;

namespace T3.Editor.Gui.Windows.RenderExport;

internal static class RenderProcess
{
    public static string LastHelpString { get; private set; } = string.Empty;

    public static double Progress => _frameCount <= 1 ? 0.0 : (_frameIndex / (double)(_frameCount - 1));
    
    public static Type? MainOutputType { get; private set; }
    public static Int2 MainOutputSize;
    public static Texture2D? MainOutputTexture;
    
    public static States State;

    // TODO: clarify the difference
    public static bool IsExporting { get; private set; }
    public static bool IsToollRenderingSomething { get; private set; }
    
    public static double ExportStartedTimeLocal;
    
    public enum States
    {
        NoOutputWindow,
        NoValidOutputType,
        NoValidOutputTexture,
        WaitingForExport,
        Exporting,
    }

    /// <remarks>
    /// needs to be called once per frame
    /// </remarks>
    public static void Update()
    {
        var outputWindow = OutputWindow.GetPrimaryOutputWindow();
        if (outputWindow == null)
        {
            State = States.NoOutputWindow;
            return;
        }

        MainOutputTexture = outputWindow.GetCurrentTexture();
        if (MainOutputTexture == null)
        {
            State = States.NoValidOutputTexture;
            return;
        }

        MainOutputType = outputWindow.ShownInstance?.Outputs.FirstOrDefault()?.ValueType;
        if (MainOutputType != typeof(Texture2D))
        {
            State = States.NoValidOutputType;
            return;
        }
        
        var desc = MainOutputTexture.Description;
        MainOutputSize.Width = desc.Width;
        MainOutputSize.Height = desc.Height;


        if (!IsExporting)
        {
            State = States.WaitingForExport;
            return;
        }

        State = States.Exporting;

        // Process frame
        bool success;
        if (_renderSettings.RenderMode == RenderSettings.RenderModes.Video)
        {
            var audioFrame = AudioRendering.GetLastMixDownBuffer(1.0 / _renderSettings.Fps);
            success = SaveVideoFrameAndAdvance( ref audioFrame, RenderAudioInfo.SoundtrackChannels(), RenderAudioInfo.SoundtrackSampleRate());
        }
        else
        {
            AudioRendering.GetLastMixDownBuffer(Playback.LastFrameDuration);
            success = SaveImageFrameAndAdvance();
        }

        // Update stats
        var effectiveFrameCount = _renderSettings.RenderMode == RenderSettings.RenderModes.Video ? _frameCount : _frameCount + 2;
        var currentFrame = _renderSettings.RenderMode == RenderSettings.RenderModes.Video ? GetRealFrame() : _frameIndex + 1;

        var completed = currentFrame >= effectiveFrameCount || !success;
        if (!completed) 
            return;

        var duration = Playback.RunTimeInSecs - _exportStartedTime;
        var successful = success ? "successfully" : "unsuccessfully";
        LastHelpString = $"Render finished {successful} in {StringUtils.HumanReadableDurationFromSeconds(duration)}";
        Log.Debug(LastHelpString);

        if (_renderSettings.AutoIncrementVersionNumber && success && _renderSettings.RenderMode == RenderSettings.RenderModes.Video)
            RenderPaths.TryIncrementVideoFileNameInUserSettings();

        Cleanup();
        IsToollRenderingSomething = false;
    }
    
    public static void TryStart(RenderSettings renderSettings)
    {
        if (IsExporting)
        {
            Log.Warning("Export is already in progress");
            return;
        }
        
        
        var targetPath = GetTargetPath(renderSettings.RenderMode);
        if (!RenderPaths.ValidateOrCreateTargetFolder(targetPath))
            return;

        IsToollRenderingSomething = true;
        ExportStartedTimeLocal = Core.Animation.Playback.RunTimeInSecs;

        _renderSettings = renderSettings;
        
        _frameIndex = 0;
        _frameCount = Math.Max(_renderSettings.FrameCount, 0);

        _exportStartedTime = Playback.RunTimeInSecs;

        if (_renderSettings.RenderMode == RenderSettings.RenderModes.Video)
        {
            _videoWriter = new Mp4VideoWriter(targetPath, MainOutputSize, _renderSettings.ExportAudio)
                               {
                                   Bitrate = _renderSettings.Bitrate,
                                   Framerate = (int)renderSettings.Fps
                               };
        }
        else
        {
            _targetFolder = targetPath;
        }

        ScreenshotWriter.ClearQueue();

        // set playback to the first frame
        RenderTiming.SetPlaybackTimeForFrame(ref _renderSettings, _frameIndex, _frameCount, ref _runtime);
        IsExporting = true;
        LastHelpString = "Rendering…";
    }

    private static int GetRealFrame() => _frameIndex - MfVideoWriter.SkipImages;
    
    
    private static string GetTargetPath(RenderSettings.RenderModes renderMode)
    {
        return renderMode == RenderSettings.RenderModes.Video
                   ? RenderPaths.ResolveProjectRelativePath(UserSettings.Config.RenderVideoFilePath)
                   : RenderPaths.ResolveProjectRelativePath(UserSettings.Config.RenderSequenceFilePath);
    }

    public static void Cancel(string? reason = null)
    {
        var duration = Playback.RunTimeInSecs - _exportStartedTime;
        LastHelpString = reason ?? $"Render cancelled after {StringUtils.HumanReadableDurationFromSeconds(duration)}";
        Cleanup();
        IsToollRenderingSomething = false;
    }

    private static void Cleanup()
    {
        IsExporting = false;

        if (_renderSettings.RenderMode == RenderSettings.RenderModes.Video)
        {
            _videoWriter?.Dispose();
            _videoWriter = null;
        }

        RenderTiming.ReleasePlaybackTime(ref _renderSettings, ref _runtime);
    }

    private static bool SaveVideoFrameAndAdvance( ref byte[] audioFrame, int channels, int sampleRate)
    {
        if (Playback.OpNotReady)
        {
            Log.Debug("Waiting for operators to complete");
            return true;
        }

        try
        {
            _videoWriter?.ProcessFrames( MainOutputTexture, ref audioFrame, channels, sampleRate);
            _frameIndex++;
            RenderTiming.SetPlaybackTimeForFrame(ref _renderSettings, _frameIndex, _frameCount, ref _runtime);
            return true;
        }
        catch (Exception e)
        {
            LastHelpString = e.ToString();
            Cleanup();
            return false;
        }
    }

    private static string GetSequenceFilePath()
    {
        var prefix = RenderPaths.SanitizeFilename(UserSettings.Config.RenderSequenceFileName);
        return Path.Combine(_targetFolder, $"{prefix}_{_frameIndex:0000}.{_renderSettings.FileFormat.ToString().ToLower()}");
    }

    private static bool SaveImageFrameAndAdvance()
    {
        if (MainOutputTexture == null)
            return false;
        
        try
        {
            var success = ScreenshotWriter.StartSavingToFile(MainOutputTexture, GetSequenceFilePath(), _renderSettings.FileFormat);
            _frameIndex++;
            RenderTiming.SetPlaybackTimeForFrame(ref _renderSettings, _frameIndex, _frameCount, ref _runtime);
            return success;
        }
        catch (Exception e)
        {
            LastHelpString = e.ToString();
            IsExporting = false;
            return false;
        }
    }

    // State
    private static Mp4VideoWriter? _videoWriter;
    private static string _targetFolder = string.Empty;
    private static double _exportStartedTime;
    private static int _frameIndex;
    private static int _frameCount;
    

    private static RenderSettings _renderSettings = null!;
    private static RenderTiming.Runtime _runtime;
    

}