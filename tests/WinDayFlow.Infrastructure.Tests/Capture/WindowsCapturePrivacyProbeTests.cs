using System.Runtime.InteropServices;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class WindowsCapturePrivacyProbeTests
{
    private const ulong Headroom = 512UL * 1024 * 1024;

    [Fact]
    public void ClearSnapshotAllowsOnlyImplementedSignals()
    {
        var nativeApi = FakeWindowsPrivacyNativeApi.CreateClear();
        var probe = CreateProbe(nativeApi);

        var signals = probe.Sample();

        Assert.Equal(NativeCapturePolicyDecision.Allow, signals.SessionUnlocked);
        Assert.Equal(NativeCapturePolicyDecision.Allow, signals.SecureDesktopClear);
        Assert.Equal(NativeCaptureConditionState.Inactive, signals.RemoteSession);
        Assert.Equal(NativeCaptureConditionState.Inactive, signals.PresentationMode);
        Assert.Equal(NativeCapturePolicyDecision.Unknown, signals.ApplicationAllowed);
        Assert.Equal(NativeCapturePolicyDecision.Unknown, signals.WindowAllowed);
        Assert.Equal(NativeCapturePolicyDecision.Allow, signals.StorageAvailable);
    }

    [Fact]
    public void ExplicitUnsafeSnapshotBlocksOrActivatesEveryImplementedSignal()
    {
        var nativeApi = FakeWindowsPrivacyNativeApi.CreateClear();
        nativeApi.SessionUnlocked = false;
        nativeApi.SecureDesktopClear = false;
        nativeApi.RemoteProtocol = WindowsRemoteProtocol.Remote;
        nativeApi.PresentationMode = true;
        nativeApi.AvailableStorageBytes = Headroom - 1;
        var probe = CreateProbe(nativeApi);

        var signals = probe.Sample();

        Assert.Equal(NativeCapturePolicyDecision.Block, signals.SessionUnlocked);
        Assert.Equal(NativeCapturePolicyDecision.Block, signals.SecureDesktopClear);
        Assert.Equal(NativeCaptureConditionState.Active, signals.RemoteSession);
        Assert.Equal(NativeCaptureConditionState.Active, signals.PresentationMode);
        Assert.Equal(NativeCapturePolicyDecision.Block, signals.StorageAvailable);
    }

    [Fact]
    public void FailedReadsRemainUnknown()
    {
        var nativeApi = new FakeWindowsPrivacyNativeApi
        {
            IsSupportedPlatform = true,
        };
        var probe = CreateProbe(nativeApi);

        var signals = probe.Sample();

        Assert.Equal(NativeCapturePrivacySignals.FailClosed, signals);
    }

    [Fact]
    public void RecoverablePolicyReadExceptionOnlyMakesTheAffectedSignalUnknown()
    {
        var nativeApi = FakeWindowsPrivacyNativeApi.CreateClear();
        nativeApi.SessionException = new InvalidOperationException("session failed");
        var probe = CreateProbe(nativeApi);

        var sessionFailure = probe.Sample();

        Assert.Equal(
            NativeCapturePolicyDecision.Unknown,
            sessionFailure.SessionUnlocked);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            sessionFailure.SecureDesktopClear);
        Assert.Equal(
            NativeCaptureConditionState.Inactive,
            sessionFailure.RemoteSession);
        Assert.Equal(
            NativeCaptureConditionState.Inactive,
            sessionFailure.PresentationMode);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            sessionFailure.StorageAvailable);

        nativeApi.SessionException = null;
        nativeApi.SecureDesktopException = new InvalidOperationException(
            "desktop failed");

        var desktopFailure = probe.Sample();

        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            desktopFailure.SessionUnlocked);
        Assert.Equal(
            NativeCapturePolicyDecision.Unknown,
            desktopFailure.SecureDesktopClear);
        Assert.Equal(
            NativeCaptureConditionState.Inactive,
            desktopFailure.RemoteSession);
        Assert.Equal(
            NativeCaptureConditionState.Inactive,
            desktopFailure.PresentationMode);
        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            desktopFailure.StorageAvailable);
    }

    [Fact]
    public void RecoverableConditionReadExceptionOnlyMakesThatSignalUnknown()
    {
        var nativeApi = FakeWindowsPrivacyNativeApi.CreateClear();
        nativeApi.PresentationException = new EntryPointNotFoundException(
            "presentation failed");
        var probe = CreateProbe(nativeApi);

        var signals = probe.Sample();

        Assert.Equal(NativeCapturePolicyDecision.Allow, signals.SessionUnlocked);
        Assert.Equal(NativeCapturePolicyDecision.Allow, signals.SecureDesktopClear);
        Assert.Equal(NativeCaptureConditionState.Inactive, signals.RemoteSession);
        Assert.Equal(NativeCaptureConditionState.Unknown, signals.PresentationMode);
        Assert.Equal(NativeCapturePolicyDecision.Allow, signals.StorageAvailable);
    }

    [Fact]
    public void RecoverableStorageReadExceptionMapsStorageToUnknown()
    {
        var nativeApi = FakeWindowsPrivacyNativeApi.CreateClear();
        nativeApi.StorageException = new IOException("storage failed");
        var probe = CreateProbe(nativeApi);

        var signals = probe.Sample();

        Assert.Equal(NativeCapturePolicyDecision.Allow, signals.SessionUnlocked);
        Assert.Equal(NativeCapturePolicyDecision.Allow, signals.SecureDesktopClear);
        Assert.Equal(NativeCaptureConditionState.Inactive, signals.RemoteSession);
        Assert.Equal(NativeCaptureConditionState.Inactive, signals.PresentationMode);
        Assert.Equal(NativeCapturePolicyDecision.Unknown, signals.StorageAvailable);
    }

    [Fact]
    public void CriticalReadExceptionsAreNotSwallowed()
    {
        var nativeApi = FakeWindowsPrivacyNativeApi.CreateClear();
        var probe = CreateProbe(nativeApi);

#pragma warning disable CA2201 // Deliberately inject runtime-reserved exceptions to verify the catch filter.
        nativeApi.SessionException = new StackOverflowException("stack failed");
        Assert.Throws<StackOverflowException>(probe.Sample);

        nativeApi.SessionException = new AccessViolationException("access failed");
        Assert.Throws<AccessViolationException>(probe.Sample);

        nativeApi.SessionException = new OutOfMemoryException("memory failed");
        Assert.Throws<OutOfMemoryException>(probe.Sample);

        nativeApi.SessionException = new SEHException("native state failed");
        Assert.Throws<SEHException>(probe.Sample);
#pragma warning restore CA2201
    }

    [Fact]
    public void RemoteSessionRequiresEveryNegativeSourceToReportLocal()
    {
        var nativeApi = FakeWindowsPrivacyNativeApi.CreateClear();
        nativeApi.RemoteProtocolRead = false;
        var probe = CreateProbe(nativeApi);

        Assert.Equal(
            NativeCaptureConditionState.Unknown,
            probe.Sample().RemoteSession);

        nativeApi.RemoteProtocolRead = true;
        nativeApi.RemoteMetricsRead = false;
        Assert.Equal(
            NativeCaptureConditionState.Unknown,
            probe.Sample().RemoteSession);
    }

    [Fact]
    public void PositiveRemoteMetricRemainsExplicitWhenProtocolReadFails()
    {
        var nativeApi = FakeWindowsPrivacyNativeApi.CreateClear();
        nativeApi.RemoteProtocolRead = false;
        nativeApi.RemoteSession = true;
        var probe = CreateProbe(nativeApi);

        Assert.Equal(
            NativeCaptureConditionState.Active,
            probe.Sample().RemoteSession);
    }

    [Fact]
    public void RemoteProtocolExceptionStillSamplesMetricsAndLaterSignals()
    {
        var nativeApi = FakeWindowsPrivacyNativeApi.CreateClear();
        nativeApi.RemoteProtocolException = new InvalidOperationException(
            "protocol failed");
        nativeApi.RemoteSession = true;
        var probe = CreateProbe(nativeApi);

        var signals = probe.Sample();

        Assert.Equal(NativeCaptureConditionState.Active, signals.RemoteSession);
        Assert.Equal(NativeCaptureConditionState.Inactive, signals.PresentationMode);
        Assert.Equal(NativeCapturePolicyDecision.Allow, signals.StorageAvailable);
    }

    [Fact]
    public void RemoteMetricsExceptionPreservesExplicitRemoteProtocol()
    {
        var nativeApi = FakeWindowsPrivacyNativeApi.CreateClear();
        nativeApi.RemoteProtocol = WindowsRemoteProtocol.Remote;
        nativeApi.RemoteMetricsException = new InvalidOperationException(
            "metrics failed");
        var probe = CreateProbe(nativeApi);

        var signals = probe.Sample();

        Assert.Equal(NativeCaptureConditionState.Active, signals.RemoteSession);
        Assert.Equal(NativeCaptureConditionState.Inactive, signals.PresentationMode);
        Assert.Equal(NativeCapturePolicyDecision.Allow, signals.StorageAvailable);
    }

    [Fact]
    public void RemoteExceptionWithoutAnotherPositiveSourceRemainsUnknown()
    {
        var nativeApi = FakeWindowsPrivacyNativeApi.CreateClear();
        nativeApi.RemoteMetricsException = new InvalidOperationException(
            "metrics failed");
        var probe = CreateProbe(nativeApi);

        Assert.Equal(
            NativeCaptureConditionState.Unknown,
            probe.Sample().RemoteSession);
    }

    [Fact]
    public void UnsupportedPlatformReturnsFailClosedWithoutSampling()
    {
        var nativeApi = FakeWindowsPrivacyNativeApi.CreateClear();
        nativeApi.IsSupportedPlatform = false;
        var probe = CreateProbe(nativeApi);

        var signals = probe.Sample();

        Assert.Equal(NativeCapturePrivacySignals.FailClosed, signals);
        Assert.Equal(0, nativeApi.ReadCount);
    }

    [Fact]
    public void RecoverablePlatformCheckExceptionReturnsFailClosedWithoutSampling()
    {
        var nativeApi = FakeWindowsPrivacyNativeApi.CreateClear();
        nativeApi.PlatformException = new InvalidOperationException(
            "platform failed");
        var probe = CreateProbe(nativeApi);

        var signals = probe.Sample();

        Assert.Equal(NativeCapturePrivacySignals.FailClosed, signals);
        Assert.Equal(0, nativeApi.ReadCount);
    }

    [Fact]
    public void StorageAtTheConfiguredHeadroomIsAvailable()
    {
        var nativeApi = FakeWindowsPrivacyNativeApi.CreateClear();
        nativeApi.AvailableStorageBytes = Headroom;
        var probe = CreateProbe(nativeApi);

        Assert.Equal(
            NativeCapturePolicyDecision.Allow,
            probe.Sample().StorageAvailable);
    }

    [Fact]
    public void ConfigurationRequiresAnAbsolutePathAndPositiveHeadroom()
    {
        var nativeApi = FakeWindowsPrivacyNativeApi.CreateClear();

        Assert.Throws<ArgumentException>(
            () => new WindowsCapturePrivacyProbe(nativeApi, "relative", Headroom));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new WindowsCapturePrivacyProbe(
                nativeApi,
                Path.GetTempPath(),
                minimumStorageHeadroomBytes: 0));
    }

    [Fact]
    public void WtsInfoExPrefixMatchesTheDocumentedX64Abi()
    {
        var layout = PInvokeWindowsPrivacyNativeApi.WtsInfoExLayout;

        Assert.Equal(8, layout.DataOffset);
        Assert.Equal(12, layout.SessionStateOffset);
        Assert.Equal(16, layout.SessionFlagsOffset);
        Assert.Equal(20, layout.MinimumBytes);
    }

    [Fact]
    public void WindowsSafeHandlesNeverThrowFromRelease()
    {
        var desktop = new SafeDesktopHandle(
            new IntPtr(1),
            _ => throw new InvalidOperationException("close failed"));
        var wtsMemory = new SafeWtsMemoryHandle(
            new IntPtr(1),
            _ => throw new InvalidOperationException("free failed"));

        Assert.Null(Record.Exception(desktop.Dispose));
        Assert.Null(Record.Exception(wtsMemory.Dispose));
    }

    [Fact]
    public void RealWindowsProbeReturnsOnlyDefinedSignalsWithoutThrowing()
    {
        var probe = new WindowsCapturePrivacyProbe(
            Path.GetTempPath(),
            Headroom);

        NativeCapturePrivacySignals? signals = null;
        var exception = Record.Exception(() => signals = probe.Sample());

        Assert.Null(exception);
        Assert.NotNull(signals);
        Assert.True(Enum.IsDefined(signals.SessionUnlocked));
        Assert.True(Enum.IsDefined(signals.SecureDesktopClear));
        Assert.True(Enum.IsDefined(signals.RemoteSession));
        Assert.True(Enum.IsDefined(signals.PresentationMode));
        Assert.True(Enum.IsDefined(signals.ApplicationAllowed));
        Assert.True(Enum.IsDefined(signals.WindowAllowed));
        Assert.True(Enum.IsDefined(signals.StorageAvailable));
    }

    private static WindowsCapturePrivacyProbe CreateProbe(
        IWindowsPrivacyNativeApi nativeApi)
    {
        return new WindowsCapturePrivacyProbe(
            nativeApi,
            Path.GetTempPath(),
            Headroom);
    }

    private sealed class FakeWindowsPrivacyNativeApi : IWindowsPrivacyNativeApi
    {
        private bool _isSupportedPlatform;

        public bool IsSupportedPlatform
        {
            get
            {
                if (PlatformException is not null)
                {
                    throw PlatformException;
                }

                return _isSupportedPlatform;
            }
            set => _isSupportedPlatform = value;
        }

        public Exception? PlatformException { get; set; }

        public bool SessionRead { get; set; }

        public bool SessionUnlocked { get; set; }

        public Exception? SessionException { get; set; }

        public bool SecureDesktopRead { get; set; }

        public bool SecureDesktopClear { get; set; }

        public Exception? SecureDesktopException { get; set; }

        public bool RemoteProtocolRead { get; set; }

        public Exception? RemoteProtocolException { get; set; }

        public WindowsRemoteProtocol RemoteProtocol { get; set; }

        public bool RemoteMetricsRead { get; set; }

        public bool RemoteSession { get; set; }

        public bool RemoteControl { get; set; }

        public Exception? RemoteMetricsException { get; set; }

        public bool PresentationRead { get; set; }

        public bool PresentationMode { get; set; }

        public Exception? PresentationException { get; set; }

        public bool StorageRead { get; set; }

        public ulong AvailableStorageBytes { get; set; }

        public Exception? StorageException { get; set; }

        public int ReadCount { get; private set; }

        public static FakeWindowsPrivacyNativeApi CreateClear()
        {
            return new FakeWindowsPrivacyNativeApi
            {
                IsSupportedPlatform = true,
                SessionRead = true,
                SessionUnlocked = true,
                SecureDesktopRead = true,
                SecureDesktopClear = true,
                RemoteProtocolRead = true,
                RemoteProtocol = WindowsRemoteProtocol.Console,
                RemoteMetricsRead = true,
                PresentationRead = true,
                StorageRead = true,
                AvailableStorageBytes = Headroom,
            };
        }

        public bool TryGetSessionUnlocked(out bool unlocked)
        {
            ReadCount++;
            if (SessionException is not null)
            {
                throw SessionException;
            }

            unlocked = SessionUnlocked;
            return SessionRead;
        }

        public bool TryGetSecureDesktopClear(out bool clear)
        {
            ReadCount++;
            if (SecureDesktopException is not null)
            {
                throw SecureDesktopException;
            }

            clear = SecureDesktopClear;
            return SecureDesktopRead;
        }

        public bool TryGetRemoteProtocol(out WindowsRemoteProtocol protocol)
        {
            ReadCount++;
            if (RemoteProtocolException is not null)
            {
                throw RemoteProtocolException;
            }

            protocol = RemoteProtocol;
            return RemoteProtocolRead;
        }

        public bool TryGetRemoteSessionMetrics(
            out bool remoteSession,
            out bool remoteControl)
        {
            ReadCount++;
            if (RemoteMetricsException is not null)
            {
                throw RemoteMetricsException;
            }

            remoteSession = RemoteSession;
            remoteControl = RemoteControl;
            return RemoteMetricsRead;
        }

        public bool TryGetPresentationMode(out bool active)
        {
            ReadCount++;
            if (PresentationException is not null)
            {
                throw PresentationException;
            }

            active = PresentationMode;
            return PresentationRead;
        }

        public bool TryGetAvailableStorageBytes(
            string directory,
            out ulong availableBytes)
        {
            _ = directory;
            ReadCount++;
            if (StorageException is not null)
            {
                throw StorageException;
            }

            availableBytes = AvailableStorageBytes;
            return StorageRead;
        }
    }
}
