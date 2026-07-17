using System.Globalization;
using System.Runtime.InteropServices;
using WinDayFlow.Application.Settings;
using WinDayFlow.Capture.Interop;
using Xunit;

namespace WinDayFlow.Infrastructure.Tests.Capture;

public sealed class WindowsCaptureTargetVerifierTests
{
    private const ulong WindowHandle = 0x1234;
    private const uint ProcessId = 42;
    private const uint ThreadId = 7;
    private const ulong CreationTime = 123_456;
    private const ulong MonitorHandle = 0x5678;
    private const string DisplayDeviceKey = @"\\.\DISPLAY1";

    [Fact]
    public void StableObservationReturnsTargetAndMatcherIdentityWithStableEpoch()
    {
        var api = FakeWindowsCaptureTargetNativeApi.CreateStable();
        var verifier = new WindowsCaptureTargetVerifier(api);

        var first = verifier.Verify();
        var second = verifier.Verify();

        Assert.Equal(NativeCaptureTargetIdentityState.Present, first.Target.State);
        Assert.Equal(WindowHandle, first.Target.WindowHandle);
        Assert.Equal(ProcessId, first.Target.ProcessId);
        Assert.Equal(CreationTime, first.Target.ProcessCreationTime100ns);
        Assert.NotEqual<ulong>(0, first.Target.TargetEpoch);
        Assert.Equal(first.Target.TargetEpoch, second.Target.TargetEpoch);
        Assert.Equal(
            WindowsCaptureDisplayTargetState.Present,
            first.DisplayTarget.State);
        Assert.Equal(MonitorHandle, first.DisplayTarget.MonitorHandle);
        Assert.Equal(DisplayDeviceKey, first.DisplayTarget.DeviceKey);
        Assert.Equal("editor.exe", first.CaptureIdentity.ExecutableName);
        Assert.Equal(
            "Contoso.Editor_123456789abcd",
            first.CaptureIdentity.PackageFamilyName);
        Assert.Equal(new string('A', 64), first.CaptureIdentity.PublisherCertificateSha256);
        Assert.Equal("Private roadmap", first.CaptureIdentity.WindowTitle);
        Assert.All(api.OpenedProcesses, process => Assert.Equal(1, process.DisposeCount));

        var applicationRuleId = Guid.NewGuid();
        var windowRuleId = Guid.NewGuid();
        var rules = new CaptureExclusionRuleSet(
        [
            CaptureExclusionRule.Create(
                applicationRuleId,
                "Editor",
                enabled: true,
                CaptureExclusionRuleScope.Application,
                ApplicationIdentityKind.ExecutableName,
                "editor.exe"),
            CaptureExclusionRule.Create(
                windowRuleId,
                "Roadmap",
                enabled: true,
                CaptureExclusionRuleScope.Window,
                ApplicationIdentityKind.ExecutableName,
                "editor.exe",
                WindowTitleMatchKind.Contains,
                "roadmap"),
        ]);

        var exclusion = NativeCaptureExclusionRuleMatcher.Evaluate(
            rules,
            first.CaptureIdentity);

        Assert.Equal(applicationRuleId, exclusion.Application.MatchedRuleId);
        Assert.Equal(windowRuleId, exclusion.Window.MatchedRuleId);
    }

    [Fact]
    public void TargetChangeAndPidReuseReceiveNewEpochs()
    {
        var api = FakeWindowsCaptureTargetNativeApi.CreateStable();
        var verifier = new WindowsCaptureTargetVerifier(api);
        var first = verifier.Verify();

        api.WindowHandle++;
        var changedWindow = verifier.Verify();

        api.ProcessFactory = () => FakeWindowsCaptureTargetProcess.CreateStable(
            ProcessId,
            CreationTime + 1);
        var reusedPid = verifier.Verify();

        Assert.True(changedWindow.Target.TargetEpoch > first.Target.TargetEpoch);
        Assert.True(reusedPid.Target.TargetEpoch > changedWindow.Target.TargetEpoch);
        Assert.Equal(ProcessId, reusedPid.Target.ProcessId);
        Assert.Equal(CreationTime + 1, reusedPid.Target.ProcessCreationTime100ns);
    }

    [Fact]
    public void AbsentOrUnknownGapInvalidatesThePreviousEpoch()
    {
        var api = FakeWindowsCaptureTargetNativeApi.CreateStable();
        var verifier = new WindowsCaptureTargetVerifier(api);
        var first = verifier.Verify();

        api.WindowHandle = 0;
        var absent = verifier.Verify();
        api.WindowHandle = WindowHandle;
        var afterAbsent = verifier.Verify();

        api.ForegroundRead = false;
        var unknown = verifier.Verify();
        api.ForegroundRead = true;
        var afterUnknown = verifier.Verify();

        Assert.Same(WindowsCaptureTargetVerificationResult.Absent, absent);
        Assert.Equal(WindowsCaptureDisplayTargetState.Absent, absent.DisplayTarget.State);
        Assert.Equal(NativeCaptureObservationState.Absent,
            absent.CaptureIdentity.WindowTitleObservation.State);
        Assert.True(afterAbsent.Target.TargetEpoch > first.Target.TargetEpoch);
        Assert.Same(WindowsCaptureTargetVerificationResult.Unknown, unknown);
        Assert.True(afterUnknown.Target.TargetEpoch > afterAbsent.Target.TargetEpoch);
    }

    [Fact]
    public void ExplicitObservationInvalidationAdvancesTheRecoveredEpoch()
    {
        var api = FakeWindowsCaptureTargetNativeApi.CreateStable();
        var verifier = new WindowsCaptureTargetVerifier(api);
        var beforeInvalidation = verifier.Verify();

        verifier.InvalidateObservation();
        var afterInvalidation = verifier.Verify();

        Assert.True(
            afterInvalidation.Target.TargetEpoch
                > beforeInvalidation.Target.TargetEpoch);
    }

    [Fact]
    public void ForegroundWindowRaceFailsClosedAndDisposesTheProcess()
    {
        var api = FakeWindowsCaptureTargetNativeApi.CreateStable();
        api.ForegroundReads.Enqueue((true, WindowHandle));
        api.ForegroundReads.Enqueue((true, WindowHandle + 1));
        var verifier = new WindowsCaptureTargetVerifier(api);

        var result = verifier.Verify();

        Assert.Same(WindowsCaptureTargetVerificationResult.Unknown, result);
        Assert.Equal(1, Assert.Single(api.OpenedProcesses).DisposeCount);
    }

    [Fact]
    public void HwndOwnerReuseDuringObservationFailsClosed()
    {
        var api = FakeWindowsCaptureTargetNativeApi.CreateStable();
        api.OwnerReads.Enqueue((true, new WindowsCaptureWindowOwner(ThreadId, ProcessId)));
        api.OwnerReads.Enqueue((true, new WindowsCaptureWindowOwner(
            ThreadId + 1,
            ProcessId + 1)));
        var verifier = new WindowsCaptureTargetVerifier(api);

        var result = verifier.Verify();

        Assert.Same(WindowsCaptureTargetVerificationResult.Unknown, result);
    }

    [Fact]
    public void DisplaySelectionChangeDuringObservationFailsClosed()
    {
        var api = FakeWindowsCaptureTargetNativeApi.CreateStable();
        api.MonitorReads.Enqueue((
            true,
            new WindowsCaptureDisplayAnchor(MonitorHandle, DisplayDeviceKey)));
        api.MonitorReads.Enqueue((
            true,
            new WindowsCaptureDisplayAnchor(MonitorHandle + 1, DisplayDeviceKey)));

        var result = new WindowsCaptureTargetVerifier(api).Verify();

        Assert.Same(WindowsCaptureTargetVerificationResult.Unknown, result);
    }

    [Fact]
    public void WindowTitleChangeDuringObservationFailsClosed()
    {
        var api = FakeWindowsCaptureTargetNativeApi.CreateStable();
        api.TitleReads.Enqueue((
            WindowsCaptureObservationReadState.Present,
            "Private roadmap"));
        api.TitleReads.Enqueue((
            WindowsCaptureObservationReadState.Present,
            "Public roadmap"));

        var result = new WindowsCaptureTargetVerifier(api).Verify();

        Assert.Same(WindowsCaptureTargetVerificationResult.Unknown, result);
    }

    [Fact]
    public void WindowTitleStateChangesDuringObservationFailClosed()
    {
        var presentToAbsentApi = FakeWindowsCaptureTargetNativeApi.CreateStable();
        presentToAbsentApi.TitleReads.Enqueue((
            WindowsCaptureObservationReadState.Present,
            "Private roadmap"));
        presentToAbsentApi.TitleReads.Enqueue((
            WindowsCaptureObservationReadState.Absent,
            string.Empty));
        var absentToPresentApi = FakeWindowsCaptureTargetNativeApi.CreateStable();
        absentToPresentApi.TitleReads.Enqueue((
            WindowsCaptureObservationReadState.Absent,
            string.Empty));
        absentToPresentApi.TitleReads.Enqueue((
            WindowsCaptureObservationReadState.Present,
            "Private roadmap"));

        Assert.Same(
            WindowsCaptureTargetVerificationResult.Unknown,
            new WindowsCaptureTargetVerifier(presentToAbsentApi).Verify());
        Assert.Same(
            WindowsCaptureTargetVerificationResult.Unknown,
            new WindowsCaptureTargetVerifier(absentToPresentApi).Verify());
    }

    [Fact]
    public void ProcessIdentityOrCreationTimeChangeFailsClosed()
    {
        var pidApi = FakeWindowsCaptureTargetNativeApi.CreateStable();
        pidApi.ProcessFactory = () => FakeWindowsCaptureTargetProcess.CreateStable(
            ProcessId,
            CreationTime) with
        {
            SecondProcessId = ProcessId + 1,
        };
        var creationApi = FakeWindowsCaptureTargetNativeApi.CreateStable();
        creationApi.ProcessFactory = () => FakeWindowsCaptureTargetProcess.CreateStable(
            ProcessId,
            CreationTime) with
        {
            SecondCreationTime = CreationTime + 1,
        };

        var pidResult = new WindowsCaptureTargetVerifier(pidApi).Verify();
        var creationResult = new WindowsCaptureTargetVerifier(creationApi).Verify();

        Assert.Same(WindowsCaptureTargetVerificationResult.Unknown, pidResult);
        Assert.Same(WindowsCaptureTargetVerificationResult.Unknown, creationResult);
    }

    [Fact]
    public void ExitedOrUnreadableProcessFailsClosed()
    {
        var exitedApi = FakeWindowsCaptureTargetNativeApi.CreateStable();
        exitedApi.ProcessFactory = () => FakeWindowsCaptureTargetProcess.CreateStable(
            ProcessId,
            CreationTime) with
        {
            SecondActive = false,
        };
        var unreadableApi = FakeWindowsCaptureTargetNativeApi.CreateStable();
        unreadableApi.ProcessFactory = () => FakeWindowsCaptureTargetProcess.CreateStable(
            ProcessId,
            CreationTime) with
        {
            CreationRead = false,
        };

        Assert.Same(
            WindowsCaptureTargetVerificationResult.Unknown,
            new WindowsCaptureTargetVerifier(exitedApi).Verify());
        Assert.Same(
            WindowsCaptureTargetVerificationResult.Unknown,
            new WindowsCaptureTargetVerifier(unreadableApi).Verify());
    }

    [Fact]
    public void RecoverableTargetApiExceptionFailsClosed()
    {
        var recoverableApi = FakeWindowsCaptureTargetNativeApi.CreateStable();
        recoverableApi.ForegroundException = new InvalidOperationException(
            "sensitive target details");

        Assert.Same(
            WindowsCaptureTargetVerificationResult.Unknown,
            new WindowsCaptureTargetVerifier(recoverableApi).Verify());
    }

    [Fact]
    public void IdentityReadFailuresRemainFieldScopedAndMalformedValuesBecomeUnknown()
    {
        var api = FakeWindowsCaptureTargetNativeApi.CreateStable();
        api.ProcessFactory = () => FakeWindowsCaptureTargetProcess.CreateStable(
            ProcessId,
            CreationTime) with
        {
            ExecutableName = @"C:\private\editor.exe",
            PackageFamilyName = "malformed package",
            CertificateException = new InvalidOperationException("certificate failed"),
        };
        api.TitleReadState = WindowsCaptureObservationReadState.Unknown;
        var verifier = new WindowsCaptureTargetVerifier(api);

        var result = verifier.Verify();

        Assert.Equal(NativeCaptureTargetIdentityState.Present, result.Target.State);
        Assert.Equal(
            NativeCaptureObservationState.Unknown,
            result.CaptureIdentity.ExecutableNameObservation.State);
        Assert.Equal(
            NativeCaptureObservationState.Unknown,
            result.CaptureIdentity.PackageFamilyNameObservation.State);
        Assert.Equal(
            NativeCaptureObservationState.Unknown,
            result.CaptureIdentity.PublisherCertificateSha256Observation.State);
        Assert.Equal(
            NativeCaptureObservationState.Unknown,
            result.CaptureIdentity.WindowTitleObservation.State);
        Assert.Null(result.CaptureIdentity.ExecutableName);
        Assert.Equal(1, Assert.Single(api.OpenedProcesses).DisposeCount);
        Assert.DoesNotContain("private", result.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnresolvedApplicationFrameHostFailsClosed()
    {
        var api = FakeWindowsCaptureTargetNativeApi.CreateStable();
        api.ProcessFactory = () => FakeWindowsCaptureTargetProcess.CreateStable(
            ProcessId,
            CreationTime) with
        {
            ExecutableName = "ApplicationFrameHost.exe",
        };

        var result = new WindowsCaptureTargetVerifier(api).Verify();

        Assert.Same(WindowsCaptureTargetVerificationResult.Unknown, result);
    }

    [Fact]
    public void TextRepresentationsRedactTargetAndIdentityValues()
    {
        var api = FakeWindowsCaptureTargetNativeApi.CreateStable();
        var result = new WindowsCaptureTargetVerifier(api).Verify();

        var text = result + " " + result.Target + " " + result.CaptureIdentity;
        text += " " + result.DisplayTarget;

        Assert.Contains("[REDACTED]", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Private roadmap", text, StringComparison.Ordinal);
        Assert.DoesNotContain("editor.exe", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Contoso.Editor", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            WindowHandle.ToString(CultureInfo.InvariantCulture),
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            CreationTime.ToString(CultureInfo.InvariantCulture),
            text,
            StringComparison.Ordinal);
        Assert.DoesNotContain(DisplayDeviceKey, text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void PInvokeProcessHandleCarriesQueryAndSynchronizeRights()
    {
        Assert.Equal(0x00101000u,
            PInvokeWindowsCaptureTargetNativeApi.TargetProcessDesiredAccess);
        var api = PInvokeWindowsCaptureTargetNativeApi.Instance;
        if (!api.IsSupportedPlatform)
        {
            return;
        }

        Assert.True(api.TryOpenProcess(
            checked((uint)Environment.ProcessId),
            out var process));
        using var opened = Assert.IsAssignableFrom<IWindowsCaptureTargetProcess>(
            process);

        Assert.True(opened.TryGetProcessId(out var processId));
        Assert.Equal(checked((uint)Environment.ProcessId), processId);
        Assert.True(opened.TryGetCreationTime100ns(out var creationTime));
        Assert.NotEqual<ulong>(0, creationTime);
        Assert.True(opened.TryGetActive(out var active));
        Assert.True(active);
        Assert.Equal(
            WindowsCaptureObservationReadState.Present,
            opened.ReadExecutableName(out var executableName));
        Assert.DoesNotContain('\\', executableName);
        Assert.DoesNotContain('/', executableName);
        var packageState = opened.ReadPackageFamilyName(out var packageFamilyName);
        Assert.True(packageState is WindowsCaptureObservationReadState.Absent
            or WindowsCaptureObservationReadState.Present);
        Assert.Equal(
            packageState == WindowsCaptureObservationReadState.Present,
            !string.IsNullOrEmpty(packageFamilyName));
        Assert.Equal(
            WindowsCaptureObservationReadState.Unknown,
            opened.ReadPublisherCertificateSha256(out _));
    }

    [Fact]
    public void NativeMonitorInfoLayoutMatchesMonitorInfoExW()
    {
        Assert.Equal(16, Marshal.SizeOf<WindowsRectangle>());
        Assert.Equal(104, Marshal.SizeOf<WindowsMonitorInfoEx>());
        Assert.Equal(0, Marshal.OffsetOf<WindowsMonitorInfoEx>(
            nameof(WindowsMonitorInfoEx.Size)).ToInt32());
        Assert.Equal(4, Marshal.OffsetOf<WindowsMonitorInfoEx>(
            nameof(WindowsMonitorInfoEx.Monitor)).ToInt32());
        Assert.Equal(20, Marshal.OffsetOf<WindowsMonitorInfoEx>(
            nameof(WindowsMonitorInfoEx.Work)).ToInt32());
        Assert.Equal(36, Marshal.OffsetOf<WindowsMonitorInfoEx>(
            nameof(WindowsMonitorInfoEx.Flags)).ToInt32());
        Assert.Equal(40, Marshal.OffsetOf<WindowsMonitorInfoEx>(
            nameof(WindowsMonitorInfoEx.DeviceName)).ToInt32());
    }

    [Fact]
    public void EpochExhaustionFailsClosedOnlyWhenANewEpochIsRequired()
    {
        var api = FakeWindowsCaptureTargetNativeApi.CreateStable();
        var verifier = new WindowsCaptureTargetVerifier(
            api,
            new ProcessWideWindowsCaptureTargetEpochSource(ulong.MaxValue - 1));

        var finalEpoch = verifier.Verify();
        var sameTarget = verifier.Verify();
        api.WindowHandle++;
        var exhausted = verifier.Verify();
        api.WindowHandle--;
        var cannotRevive = verifier.Verify();

        Assert.Equal(ulong.MaxValue, finalEpoch.Target.TargetEpoch);
        Assert.Equal(ulong.MaxValue, sameTarget.Target.TargetEpoch);
        Assert.Same(WindowsCaptureTargetVerificationResult.Unknown, exhausted);
        Assert.Same(WindowsCaptureTargetVerificationResult.Unknown, cannotRevive);
    }

    [Fact]
    public void VerifierRecreationCannotReuseAnEpochFromTheSameSource()
    {
        var epochSource = new ProcessWideWindowsCaptureTargetEpochSource(40);
        var first = new WindowsCaptureTargetVerifier(
            FakeWindowsCaptureTargetNativeApi.CreateStable(),
            epochSource).Verify();
        var second = new WindowsCaptureTargetVerifier(
            FakeWindowsCaptureTargetNativeApi.CreateStable(),
            epochSource).Verify();

        Assert.Equal<ulong>(41, first.Target.TargetEpoch);
        Assert.Equal<ulong>(42, second.Target.TargetEpoch);
    }

    [Fact]
    public void UnsupportedPlatformShortCircuitsWithoutNativeReads()
    {
        var api = FakeWindowsCaptureTargetNativeApi.CreateStable();
        api.IsSupportedPlatform = false;
        api.ForegroundException = new InvalidOperationException(
            "The foreground API must not be called.");

        var result = new WindowsCaptureTargetVerifier(api).Verify();

        Assert.Same(WindowsCaptureTargetVerificationResult.Unknown, result);
        Assert.Empty(api.OpenedProcesses);
    }

    [Fact]
    public void OwnerThreadAndDisplayChangesReceiveNewEpochs()
    {
        var api = FakeWindowsCaptureTargetNativeApi.CreateStable();
        var verifier = new WindowsCaptureTargetVerifier(api);
        var first = verifier.Verify();

        api.Owner = new WindowsCaptureWindowOwner(ThreadId + 1, ProcessId);
        var changedThread = verifier.Verify();
        api.DisplayTarget = new WindowsCaptureDisplayAnchor(
            MonitorHandle + 1,
            DisplayDeviceKey);
        var changedMonitor = verifier.Verify();
        api.DisplayTarget = new WindowsCaptureDisplayAnchor(
            MonitorHandle + 1,
            @"\\.\DISPLAY2");
        var changedDeviceKey = verifier.Verify();

        Assert.True(changedThread.Target.TargetEpoch > first.Target.TargetEpoch);
        Assert.True(changedMonitor.Target.TargetEpoch > changedThread.Target.TargetEpoch);
        Assert.True(changedDeviceKey.Target.TargetEpoch > changedMonitor.Target.TargetEpoch);
    }

    [Fact]
    public void OversizedDisplayDeviceKeyFailsClosed()
    {
        var api = FakeWindowsCaptureTargetNativeApi.CreateStable();
        api.DisplayTarget = new WindowsCaptureDisplayAnchor(
            MonitorHandle,
            new string('D', WindowsCaptureDisplayTarget.MaximumDeviceKeyCharacters + 1));

        var result = new WindowsCaptureTargetVerifier(api).Verify();

        Assert.Same(WindowsCaptureTargetVerificationResult.Unknown, result);
        Assert.Empty(api.OpenedProcesses);
    }

    [Fact]
    public void PackageFamilyObservationsPreservePresentAbsentAndUnknown()
    {
        var presentApi = FakeWindowsCaptureTargetNativeApi.CreateStable();
        var absentApi = FakeWindowsCaptureTargetNativeApi.CreateStable();
        absentApi.ProcessFactory = () => FakeWindowsCaptureTargetProcess.CreateStable(
            ProcessId,
            CreationTime) with
        {
            PackageFamilyNameState = WindowsCaptureObservationReadState.Absent,
            PackageFamilyName = string.Empty,
        };
        var unknownApi = FakeWindowsCaptureTargetNativeApi.CreateStable();
        unknownApi.ProcessFactory = () => FakeWindowsCaptureTargetProcess.CreateStable(
            ProcessId,
            CreationTime) with
        {
            PackageFamilyNameState = WindowsCaptureObservationReadState.Unknown,
            PackageFamilyName = string.Empty,
        };

        var present = new WindowsCaptureTargetVerifier(presentApi).Verify();
        var absent = new WindowsCaptureTargetVerifier(absentApi).Verify();
        var unknown = new WindowsCaptureTargetVerifier(unknownApi).Verify();

        Assert.Equal(
            NativeCaptureObservationState.Present,
            present.CaptureIdentity.PackageFamilyNameObservation.State);
        Assert.Equal(
            NativeCaptureObservationState.Absent,
            absent.CaptureIdentity.PackageFamilyNameObservation.State);
        Assert.Equal(
            NativeCaptureObservationState.Unknown,
            unknown.CaptureIdentity.PackageFamilyNameObservation.State);
    }

    [Fact]
    public async Task ConcurrentVerificationRetainsOneStableEpoch()
    {
        var api = FakeWindowsCaptureTargetNativeApi.CreateStable();
        var verifier = new WindowsCaptureTargetVerifier(api);

        var results = await Task.WhenAll(Enumerable.Range(0, 64)
            .Select(_ => Task.Run(verifier.Verify)));

        var epoch = Assert.Single(results.Select(result => result.Target.TargetEpoch)
            .Distinct());
        Assert.NotEqual<ulong>(0, epoch);
        Assert.All(results, result => Assert.Equal(
            NativeCaptureTargetIdentityState.Present,
            result.Target.State));
        Assert.All(api.OpenedProcesses, process => Assert.Equal(1, process.DisposeCount));
    }

    [Fact]
    public async Task ConcurrentEpochResolutionStrictlyOrdersDifferentFingerprints()
    {
        var epochSource = new ProcessWideWindowsCaptureTargetEpochSource();
        var fingerprints = Enumerable.Range(1, 64)
            .Select(index => new WindowsCaptureTargetFingerprint(
                WindowHandle + checked((ulong)index),
                ProcessId,
                CreationTime,
                ThreadId,
                MonitorHandle,
                DisplayDeviceKey))
            .ToArray();

        var epochs = await Task.WhenAll(fingerprints.Select(fingerprint => Task.Run(() =>
        {
            Assert.True(epochSource.TryResolve(fingerprint, out var epoch));
            return epoch;
        })));

        Assert.Equal(
            Enumerable.Range(1, fingerprints.Length).Select(index => (ulong)index),
            epochs.Order());
    }

    [Fact]
    public void AGapObservedByAnotherVerifierCannotReviveAnOldEpoch()
    {
        var epochSource = new ProcessWideWindowsCaptureTargetEpochSource(100);
        var firstApi = FakeWindowsCaptureTargetNativeApi.CreateStable();
        var secondApi = FakeWindowsCaptureTargetNativeApi.CreateStable();
        var firstVerifier = new WindowsCaptureTargetVerifier(firstApi, epochSource);
        var beforeGap = firstVerifier.Verify();
        var secondVerifier = new WindowsCaptureTargetVerifier(secondApi, epochSource);

        secondApi.ForegroundRead = false;
        Assert.Same(
            WindowsCaptureTargetVerificationResult.Unknown,
            secondVerifier.Verify());
        var afterGap = firstVerifier.Verify();

        Assert.True(afterGap.Target.TargetEpoch > beforeGap.Target.TargetEpoch);
    }

    private sealed class FakeWindowsCaptureTargetNativeApi
        : IWindowsCaptureTargetNativeApi
    {
        public bool IsSupportedPlatform { get; set; } = true;

        public bool ForegroundRead { get; set; } = true;

        public ulong WindowHandle { get; set; } = WindowsCaptureTargetVerifierTests.WindowHandle;

        public Exception? ForegroundException { get; set; }

        public Queue<(bool Read, ulong WindowHandle)> ForegroundReads { get; } = new();

        public bool OwnerRead { get; set; } = true;

        public WindowsCaptureWindowOwner Owner { get; set; } = new(ThreadId, ProcessId);

        public Queue<(bool Read, WindowsCaptureWindowOwner Owner)> OwnerReads { get; } = new();

        public bool ProcessOpen { get; set; } = true;

        public bool MonitorRead { get; set; } = true;

        public WindowsCaptureDisplayAnchor DisplayTarget { get; set; } = new(
            MonitorHandle,
            DisplayDeviceKey);

        public Queue<(bool Read, WindowsCaptureDisplayAnchor DisplayTarget)>
            MonitorReads
        { get; } = new();

        public Func<FakeWindowsCaptureTargetProcess> ProcessFactory { get; set; } =
            () => FakeWindowsCaptureTargetProcess.CreateStable(ProcessId, CreationTime);

        public List<FakeWindowsCaptureTargetProcess> OpenedProcesses { get; } = [];

        public WindowsCaptureObservationReadState TitleReadState { get; set; } =
            WindowsCaptureObservationReadState.Present;

        public string Title { get; set; } = "Private roadmap";

        public Queue<(WindowsCaptureObservationReadState State, string Value)>
            TitleReads
        { get; } = new();

        public Exception? TitleException { get; set; }

        public static FakeWindowsCaptureTargetNativeApi CreateStable()
        {
            return new FakeWindowsCaptureTargetNativeApi();
        }

        public bool TryGetForegroundWindow(out ulong windowHandle)
        {
            if (ForegroundException is not null)
            {
                throw ForegroundException;
            }

            if (ForegroundReads.TryDequeue(out var read))
            {
                windowHandle = read.WindowHandle;
                return read.Read;
            }

            windowHandle = WindowHandle;
            return ForegroundRead;
        }

        public bool TryGetWindowOwner(
            ulong windowHandle,
            out WindowsCaptureWindowOwner owner)
        {
            _ = windowHandle;
            if (OwnerReads.TryDequeue(out var read))
            {
                owner = read.Owner;
                return read.Read;
            }

            owner = Owner;
            return OwnerRead;
        }

        public bool TryOpenProcess(
            uint processId,
            out IWindowsCaptureTargetProcess? process)
        {
            _ = processId;
            if (!ProcessOpen)
            {
                process = null;
                return false;
            }

            var opened = ProcessFactory();
            OpenedProcesses.Add(opened);
            process = opened;
            return true;
        }

        public bool TryGetDisplayTarget(
            ulong windowHandle,
            out WindowsCaptureDisplayAnchor displayTarget)
        {
            _ = windowHandle;
            if (MonitorReads.TryDequeue(out var read))
            {
                displayTarget = read.DisplayTarget;
                return read.Read;
            }

            displayTarget = DisplayTarget;
            return MonitorRead;
        }

        public WindowsCaptureObservationReadState ReadWindowTitle(
            ulong windowHandle,
            out string value)
        {
            _ = windowHandle;
            if (TitleException is not null)
            {
                throw TitleException;
            }

            if (TitleReads.TryDequeue(out var read))
            {
                value = read.Value;
                return read.State;
            }

            value = Title;
            return TitleReadState;
        }
    }

    private sealed record FakeWindowsCaptureTargetProcess
        : IWindowsCaptureTargetProcess
    {
        public required uint ProcessId { get; init; }

        public uint? SecondProcessId { get; init; }

        public bool ProcessIdRead { get; init; } = true;

        public required ulong CreationTime { get; init; }

        public ulong? SecondCreationTime { get; init; }

        public bool CreationRead { get; init; } = true;

        public bool FirstActive { get; init; } = true;

        public bool SecondActive { get; init; } = true;

        public bool ActiveRead { get; init; } = true;

        public WindowsCaptureObservationReadState ExecutableNameState { get; init; } =
            WindowsCaptureObservationReadState.Present;

        public string ExecutableName { get; init; } = "editor.exe";

        public WindowsCaptureObservationReadState PackageFamilyNameState { get; init; } =
            WindowsCaptureObservationReadState.Present;

        public string PackageFamilyName { get; init; } =
            "Contoso.Editor_123456789abcd";

        public WindowsCaptureObservationReadState CertificateState { get; init; } =
            WindowsCaptureObservationReadState.Present;

        public string CertificateSha256 { get; init; } = new('a', 64);

        public Exception? CertificateException { get; init; }

        public int DisposeCount { get; private set; }

        private int ProcessIdReadCount { get; set; }

        private int CreationReadCount { get; set; }

        private int ActiveReadCount { get; set; }

        public static FakeWindowsCaptureTargetProcess CreateStable(
            uint processId,
            ulong creationTime)
        {
            return new FakeWindowsCaptureTargetProcess
            {
                ProcessId = processId,
                CreationTime = creationTime,
            };
        }

        public bool TryGetProcessId(out uint processId)
        {
            ProcessIdReadCount++;
            processId = ProcessIdReadCount == 1
                ? ProcessId
                : SecondProcessId ?? ProcessId;
            return ProcessIdRead;
        }

        public bool TryGetCreationTime100ns(out ulong creationTime100ns)
        {
            CreationReadCount++;
            creationTime100ns = CreationReadCount == 1
                ? CreationTime
                : SecondCreationTime ?? CreationTime;
            return CreationRead;
        }

        public bool TryGetActive(out bool active)
        {
            ActiveReadCount++;
            active = ActiveReadCount == 1 ? FirstActive : SecondActive;
            return ActiveRead;
        }

        public WindowsCaptureObservationReadState ReadExecutableName(out string value)
        {
            value = ExecutableName;
            return ExecutableNameState;
        }

        public WindowsCaptureObservationReadState ReadPackageFamilyName(out string value)
        {
            value = PackageFamilyName;
            return PackageFamilyNameState;
        }

        public WindowsCaptureObservationReadState ReadPublisherCertificateSha256(
            out string value)
        {
            if (CertificateException is not null)
            {
                throw CertificateException;
            }

            value = CertificateSha256;
            return CertificateState;
        }

        public void Dispose()
        {
            DisposeCount++;
        }
    }
}
