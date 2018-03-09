#addin nuget:?package=Cake.AppleSimulator
#addin nuget:?package=Cake.Android.Adb&version=2.0.6
#addin nuget:?package=Cake.Android.AvdManager&version=1.0.2
#addin nuget:?package=Cake.FileHelpers

var TARGET = Argument("target", "Default");

var IOS_SIM_NAME = EnvironmentVariable("IOS_SIM_NAME") ?? "iPhone X";
var IOS_SIM_RUNTIME = EnvironmentVariable("IOS_SIM_RUNTIME") ?? "iOS 11.1";
var IOS_PROJ = "./Caboodle.DeviceTests.iOS/Caboodle.DeviceTests.iOS.csproj";
var IOS_BUNDLE_ID = "com.xamarin.caboodle.devicetests";
var IOS_IPA_PATH = "./Caboodle.DeviceTests.iOS/bin/iPhoneSimulator/Release/CaboodleDeviceTestsiOS.app";
var IOS_TEST_RESULTS_PATH = "./nunit-ios.xml";

var ANDROID_PROJ = "./Caboodle.DeviceTests.Android/Caboodle.DeviceTests.Android.csproj";
var ANDROID_APK_PATH = "./Caboodle.DeviceTests.Android/bin/Release/com.xamarin.caboodle.devicetests-Signed.apk";
var ANDROID_TEST_RESULTS_PATH = "./nunit-android.xml";
var ANDROID_AVD = "CABOODLE";
var ANDROID_PKG_NAME = "com.xamarin.caboodle.devicetests";
var ANDROID_EMU_TARGET = EnvironmentVariable("ANDROID_EMU_TARGET") ?? "system-images;android-26;google_apis;x86";
var ANDROID_EMU_DEVICE = EnvironmentVariable("ANDROID_EMU_DEVICE") ?? "Nexus 5X";

var UWP_PROJ = "./Caboodle.DeviceTests.UWP/Caboodle.DeviceTests.UWP.csproj";
var UWP_TEST_RESULTS_PATH = "./nunit-uwp.xml";
var UWP_PACKAGE_ID = "ec0cc741-fd3e-485c-81be-68815c480690";

var TCP_LISTEN_PORT = 10578;
var TCP_LISTEN_HOST = System.Net.Dns.GetHostEntry(System.Net.Dns.GetHostName())
        .AddressList.First(f => f.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork).ToString();

Func<int, FilePath, Task> DownloadTcpTextAsync = (int port, FilePath filename) =>
    System.Threading.Tasks.Task.Run (() => {
        var tcpListener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Any, port);
        tcpListener.Start();

        var tcpClient = tcpListener.AcceptTcpClient();
        var fileName = MakeAbsolute (filename).FullPath;

        using (var file = System.IO.File.Open(fileName, System.IO.FileMode.Create))
        using (var stream = tcpClient.GetStream())
            stream.CopyTo(file);
    });

Task ("build-ios")
    .Does (() =>
{
    // Setup the test listener config to be built into the app
    FileWriteText((new FilePath(IOS_PROJ)).GetDirectory().CombineWithFilePath("tests.cfg"), $"{TCP_LISTEN_HOST}:{TCP_LISTEN_PORT}");

    // Nuget restore
    MSBuild (IOS_PROJ, c => {
        c.Configuration = "Release";
        c.Targets.Clear();
        c.Targets.Add("Restore");
    });

    // Build the project (with ipa)
    MSBuild (IOS_PROJ, c => {
        c.Configuration = "Release";
        c.Properties["Platform"] = new List<string> { "iPhoneSimulator" };
        c.Properties["BuildIpa"] = new List<string> { "true" };
        c.Targets.Clear();
        c.Targets.Add("Rebuild");
    });
});

Task ("test-ios-emu")
    .IsDependentOn ("build-ios")
    .Does (() =>
{
    // Look for a matching simulator on the system
    var sim = ListAppleSimulators ()
        .First (s => (s.Availability.Contains("available") || s.Availability.Contains("booted"))
                && s.Name == IOS_SIM_NAME && s.Runtime == IOS_SIM_RUNTIME);

    // Boot the simulator
    Information("Booting: {0} ({1} - {2})", sim.Name, sim.Runtime, sim.UDID);
    if (!sim.State.ToLower().Contains ("booted"))
        BootAppleSimulator (sim.UDID);

    // Wait for it to be booted
    var booted = false;
    for (int i = 0; i < 100; i++) {
        if (ListAppleSimulators().Any (s => s.UDID == sim.UDID && s.State.ToLower().Contains("booted"))) {
            booted = true;
            break;
        }
        System.Threading.Thread.Sleep(1000);
    }

    // Install the IPA that was previously built
    var ipaPath = new FilePath(IOS_IPA_PATH);
    Information ("Installing: {0}", ipaPath);
    InstalliOSApplication(sim.UDID, MakeAbsolute(ipaPath).FullPath);

    // Start our Test Results TCP listener
    Information("Started TCP Test Results Listener on port: {0}", TCP_LISTEN_PORT);
    var tcpListenerTask = DownloadTcpTextAsync (TCP_LISTEN_PORT, IOS_TEST_RESULTS_PATH);

    // Launch the IPA
    Information("Launching: {0}", IOS_BUNDLE_ID);
    LaunchiOSApplication(sim.UDID, IOS_BUNDLE_ID);

    // Wait for the TCP listener to get results
    Information("Waiting for tests...");
    tcpListenerTask.Wait ();

    // Close up simulators
    Information("Closing Simulator");
    ShutdownAllAppleSimulators ();
});


Task ("build-android")
    .Does (() =>
{
    // Nuget restore
    MSBuild (ANDROID_PROJ, c => {
        c.Configuration = "Debug";
        c.Targets.Clear();
        c.Targets.Add("Restore");
    });

    // Build the app in debug mode
    // needs to be debug so unit tests get discovered
    MSBuild (ANDROID_PROJ, c => {
        c.Configuration = "Debug";
        c.Targets.Clear();
        c.Targets.Add("Rebuild");
    });
});

Task ("test-android-emu")
    .IsDependentOn ("build-android")
    .Does (() =>
{
    if (EnvironmentVariable("ANDROID_SKIP_AVD_CREATE") == null) {
        // Create the AVD if necessary
        Information ("Creating AVD if necessary: {0}...", ANDROID_AVD);
        if (!AndroidAvdListAvds ().Any (a => a.Name == ANDROID_AVD))
            AndroidAvdCreate (ANDROID_AVD, ANDROID_EMU_TARGET, ANDROID_EMU_DEVICE, force: true);
    }

    // Start up the emulator by name
    Information ("Starting Emulator: {0}...", ANDROID_AVD);
    var emu = StartAndReturnProcess ("emulator", new ProcessSettings { 
        Arguments = $"-avd {ANDROID_AVD}" });

    // Keep checking adb for an emulator with an AVD name matching the one we just started
    var emuSerial = string.Empty;
    for (int i = 0; i < 100; i++) {
        foreach (var device in AdbDevices().Where(d => d.Serial.StartsWith("emulator-"))) {
            if (AdbGetAvdName(device.Serial).Equals(ANDROID_AVD, StringComparison.OrdinalIgnoreCase)) {
                emuSerial = device.Serial;
                break;
            }
        }

        if (!string.IsNullOrEmpty(emuSerial))
            break;
        else
            System.Threading.Thread.Sleep(1000);
    }

    Information ("Matched ADB Serial: {0}", emuSerial);
    var adbSettings = new AdbToolSettings { Serial = emuSerial };

    // Wait for the emulator to enter a 'booted' state
    AdbWaitForEmulatorToBoot(TimeSpan.FromSeconds(100), adbSettings);
    Information ("Emulator finished booting.");

    // Try uninstalling the existing package (if installed)
    try { 
        AdbUninstall (ANDROID_PKG_NAME, false, adbSettings);
        Information ("Uninstalled old: {0}", ANDROID_PKG_NAME);
    } catch { }

    // Use the Install target to push the app onto emulator
    MSBuild (ANDROID_PROJ, c => {
        c.Configuration = "Debug";
        c.Properties["AdbTarget"] = new List<string> { "-s " + emuSerial };
        c.Targets.Clear();
        c.Targets.Add("Install");
    });

    // Start the TCP Test results listener
    Information("Started TCP Test Results Listener on port: {0}:{1}", TCP_LISTEN_HOST, TCP_LISTEN_PORT);
    var tcpListenerTask = DownloadTcpTextAsync (TCP_LISTEN_PORT, ANDROID_TEST_RESULTS_PATH);

    // Launch the app on the emulator
    AdbShell ($"am start -n {ANDROID_PKG_NAME}/{ANDROID_PKG_NAME}.MainActivity --es HOST_IP {TCP_LISTEN_HOST} --ei HOST_PORT {TCP_LISTEN_PORT}", adbSettings);

    // Wait for the test results to come back
    Information("Waiting for tests...");
    tcpListenerTask.Wait ();

    // Close emulator
    emu.Kill();
});


Task ("build-uwp")
    .Does (() =>
{
    // Nuget restore
    MSBuild (UWP_PROJ, c => {
        c.Targets.Clear();
        c.Targets.Add("Restore");
    });

    // Build the project (with ipa)
    MSBuild (UWP_PROJ, c => {
        c.Configuration = "Debug";
        c.Properties["AppxBundlePlatforms"] = new List<string> { "x86" };
        c.Properties["AppxBundle"] = new List<string> { "Always" };
        c.Targets.Clear();
        c.Targets.Add("Rebuild");
    });
});


Task ("test-uwp-emu")
    .IsDependentOn ("build-uwp")
    .WithCriteria(IsRunningOnWindows())
    .Does (() =>
{
    var uninstallPS = new Action (() => {
        StartProcess ("powershell", $"Remove-AppxPackage -Package (Get-AppxPackage -Name {UWP_PACKAGE_ID}).PackageFullName");
    });

    var appxBundlePath = GetFiles("./**/AppPackages/**/*.appxbundle").First ();

    try {
        // Try to uninstall the app if it exists from before
        uninstallPS();
    } catch { }

    // Install the appx
    Information("Installing appx: {0}", appxBundlePath);
    StartProcess ("powershell", "Add-AppxPackage -Path \"" + MakeAbsolute(appxBundlePath).FullPath + "\"");

    // Start the TCP Test results listener
    Information("Started TCP Test Results Listener on port: {0}:{1}", TCP_LISTEN_HOST, TCP_LISTEN_PORT);
    var tcpListenerTask = DownloadTcpTextAsync (TCP_LISTEN_PORT, UWP_TEST_RESULTS_PATH);

    // Launch the app
    Information("Running appx: {0}", appxBundlePath);
    System.Diagnostics.Process.Start($"caboodle-device-tests://?host_ip={TCP_LISTEN_HOST}&host_port={TCP_LISTEN_PORT}");

    // Wait for the test results to come back
    Information("Waiting for tests...");
    tcpListenerTask.Wait ();

    // Uninstall the app (this will terminate it too)
    uninstallPS();
});

RunTarget(TARGET);