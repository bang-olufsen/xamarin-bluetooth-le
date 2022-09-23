using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.OS;
using Java.Util;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Extensions;
using Plugin.BLE.Abstractions.EventArgs;
using BC.Mobile.Managers;
using BC.Mobile.Strings;
using Unity;
using BC.Mobile.Utilities.Messaging;
using Object = Java.Lang.Object;
using Trace = Plugin.BLE.Abstractions.Trace;
using BC.Mobile.Logging;

namespace Plugin.BLE.Android
{
    public class Adapter : AdapterBase
    {
        private readonly BluetoothManager _bluetoothManager;
        private readonly BluetoothAdapter _bluetoothAdapter;
        private readonly Api18BleScanCallback _api18ScanCallback;
        private readonly Api21BleScanCallback _api21ScanCallback;

        public override IList<IDevice> ConnectedDevices => ConnectedDeviceRegistry.Values.ToList();
        private static readonly ILogger _logger = LoggerFactory.CreateLogger(nameof(Adapter));

        /// <summary>
        /// Used to store all connected devices
        /// </summary>
        public Dictionary<string, IDevice> ConnectedDeviceRegistry { get; }

        /// <summary>
        ///  Thread safety
        /// </summary>
        public object ConnectedDeviceRegistryLock { get; } = new object();

        public Adapter(BluetoothManager bluetoothManager)
        {
            _bluetoothManager = bluetoothManager;
            _bluetoothAdapter = bluetoothManager.Adapter;

            ConnectedDeviceRegistry = new Dictionary<string, IDevice>();

            // TODO: bonding
            //var bondStatusBroadcastReceiver = new BondStatusBroadcastReceiver();
            //Application.Context.RegisterReceiver(bondStatusBroadcastReceiver,
            //    new IntentFilter(BluetoothDevice.ActionBondStateChanged));

            ////forward events from broadcast receiver
            //bondStatusBroadcastReceiver.BondStateChanged += (s, args) =>
            //{
            //    //DeviceBondStateChanged(this, args);
            //};

            if (Build.VERSION.SdkInt >= BuildVersionCodes.Lollipop)
            {
                _api21ScanCallback = new Api21BleScanCallback(this);
            }
            else
            {
                _api18ScanCallback = new Api18BleScanCallback(this);
            }
        }

        protected override Task StartScanningForDevicesNativeAsync(Guid[] serviceUuids, bool allowDuplicatesKey, CancellationToken scanCancellationToken)
        {
            // clear out the list
            DiscoveredDevices.Clear();

            if (Build.VERSION.SdkInt < BuildVersionCodes.Lollipop)
            {
                StartScanningOld(serviceUuids);
            }
            else
            {
                StartScanningNew(serviceUuids);
            }

            return Task.FromResult(true);
        }

        private void StartScanningOld(Guid[] serviceUuids)
        {
            var hasFilter = serviceUuids?.Any() ?? false;
            UUID[] uuids = null;
            if (hasFilter)
            {
                uuids = serviceUuids.Select(u => UUID.FromString(u.ToString())).ToArray();
            }
            Trace.Message("Adapter < 21: Starting a scan for devices.");
#pragma warning disable 618
            _bluetoothAdapter.StartLeScan(uuids, _api18ScanCallback);
#pragma warning restore 618
        }

        private void StartScanningNew(Guid[] serviceUuids)
        {
            var hasFilter = serviceUuids?.Any() ?? false;
            List<ScanFilter> scanFilters = null;

            if (hasFilter)
            {
                scanFilters = new List<ScanFilter>();
                foreach (var serviceUuid in serviceUuids)
                {
                    var sfb = new ScanFilter.Builder();
                    sfb.SetServiceUuid(ParcelUuid.FromString(serviceUuid.ToString()));
                    scanFilters.Add(sfb.Build());
                }
            }

            var ssb = new ScanSettings.Builder()
                .SetScanMode(ScanMode.ToNative())
                .SetCallbackType(ScanCallbackType.AllMatches)
                .SetMatchMode(BluetoothScanMatchMode.Aggressive)
                .SetNumOfMatches((int)BluetoothScanMatchNumber.OneAdvertisement)
                .SetReportDelay(0);

            if (_bluetoothAdapter.BluetoothLeScanner != null)
            {
                Trace.Message($"Adapter >=21: Starting a scan for devices. ScanMode: {ScanMode}");
                if (hasFilter)
                {
                    Trace.Message($"ScanFilters: {string.Join(", ", serviceUuids)}");
                }
                _bluetoothAdapter.BluetoothLeScanner.StartScan(scanFilters, ssb.Build(), _api21ScanCallback);
            }
            else
            {
                Trace.Message("Adapter >= 21: Scan failed. Bluetooth is probably off");
            }
        }

        protected override void StopScanNative()
        {
            if (Build.VERSION.SdkInt < BuildVersionCodes.Lollipop)
            {
                Trace.Message("Adapter < 21: Stopping the scan for devices.");
#pragma warning disable 618
                _bluetoothAdapter.StopLeScan(_api18ScanCallback);
#pragma warning restore 618
            }
            else
            {
                Trace.Message("Adapter >= 21: Stopping the scan for devices.");
                _bluetoothAdapter.BluetoothLeScanner?.StopScan(_api21ScanCallback);
            }
        }

        protected override Task ConnectToDeviceNativeAsync(IDevice device, ConnectParameters connectParameters,
            CancellationToken cancellationToken)
        {
            ((Device)device).Connect(connectParameters);
            return Task.CompletedTask;
        }

        protected override void DisconnectDeviceNative(IDevice device)
        {
            //make sure everything is disconnected
            ((Device)device).Disconnect();
        }

        public override async Task<IDevice> ConnectToKnownDeviceAsync(Guid deviceGuid, ConnectParameters connectParameters = default(ConnectParameters), CancellationToken cancellationToken = default(CancellationToken))
        {
            var macBytes = deviceGuid.ToByteArray().Skip(10).Take(6).ToArray();
            var nativeDevice = _bluetoothAdapter.GetRemoteDevice(macBytes);

            var device = new Device(this, nativeDevice, null, 0, new byte[] { });

            await ConnectToDeviceAsync(device, connectParameters, cancellationToken);
            return device;
        }

        public override List<IDevice> GetSystemConnectedOrPairedDevices(Guid[] services = null)
        {
            if (services != null)
            {
                Trace.Message("Caution: GetSystemConnectedDevices does not take into account the 'services' parameter on Android.");
            }

            //add dualMode type too as they are BLE too ;)
            var connectedDevices = _bluetoothManager.GetConnectedDevices(ProfileType.Gatt).Where(d => d.Type == BluetoothDeviceType.Le || d.Type == BluetoothDeviceType.Dual);

            var bondedDevices = _bluetoothAdapter.BondedDevices.Where(d => d.Type == BluetoothDeviceType.Le || d.Type == BluetoothDeviceType.Dual);

            return connectedDevices.Union(bondedDevices, new DeviceComparer()).Select(d => new Device(this, d, null, 0)).Cast<IDevice>().ToList();
        }

        protected override async Task<IDevice> ConnectNativeAsync(Guid uuid, Func<IDevice, bool> deviceFilter, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (uuid == Guid.Empty)
            {
                var stopToken = new CancellationTokenSource();
                var linkedToken = CancellationTokenSource.CreateLinkedTokenSource(stopToken.Token, cancellationToken).Token;
                var taskCompletionSource = new TaskCompletionSource<IDevice>();
                EventHandler<DeviceEventArgs> handler = (sender, args) =>
                {
                    var device = args.Device;

                    if (deviceFilter(device) == false)
                    {
                        return;
                    }

                    if (taskCompletionSource.TrySetResult(device))
                    {
                        stopToken.Cancel();
                    }
                };

                try
                {
                    DeviceDiscovered += handler;
                    async Task<IDevice> WaitAsync()
                    {
                        await Task.Delay(ScanTimeout);
                        return null;
                    }

                    var scanTask = StartScanningForDevicesAsync(deviceFilter: deviceFilter, cancellationToken: linkedToken);
                    var device = await await Task.WhenAny(taskCompletionSource.Task, WaitAsync());

                    //Stop scanning when we timeout on waiting for an result
                    stopToken.Cancel();

                    // make sure to wait for the scan to complete before connecting to avoid doing multiple things at once. If a
                    // device was matched, then it should already have completed through the cancellation, otherwise it is
                    // controlled by the scan timeout
                    await scanTask;

                    await ConnectToDeviceAsync(device, new ConnectParameters(false, true), cancellationToken);
                    return device;
                }
                finally
                {
                    DeviceDiscovered -= handler;
                }
            }
            else
            {                
                return await ConnectToKnownDeviceAsync(uuid, new ConnectParameters(false, true), cancellationToken);
            }
        }

        private class DeviceComparer : IEqualityComparer<BluetoothDevice>
        {
            public bool Equals(BluetoothDevice x, BluetoothDevice y)
            {
                return x.Address == y.Address;
            }

            public int GetHashCode(BluetoothDevice obj)
            {
                return obj.GetHashCode();
            }
        }


        public class Api18BleScanCallback : Object, BluetoothAdapter.ILeScanCallback
        {
            private readonly Adapter _adapter;

            public Api18BleScanCallback(Adapter adapter)
            {
                _adapter = adapter;
            }

            public void OnLeScan(BluetoothDevice bleDevice, int rssi, byte[] scanRecord)
            {
                Trace.Message("Adapter.LeScanCallback: " + bleDevice.Name);

                _adapter.HandleDiscoveredDevice(new Device(_adapter, bleDevice, null, rssi, scanRecord));
            }
        }


        public class Api21BleScanCallback : ScanCallback
        {
            private readonly Adapter _adapter;
            public Api21BleScanCallback(Adapter adapter)
            {
                _adapter = adapter;
            }

            public override void OnScanFailed(ScanFailure errorCode)
            {
                Trace.Message("Adapter: Scan failed with code {0}", errorCode);
                _logger.Warn("Adapter: scan failed with code: " + errorCode);

                base.OnScanFailed(errorCode);

                //Same errorcode as shown on LightBlue app ("SCAN_FAILED_APPLICATION_REGISTRATION_FAILED")
                //Will trigger if BLE scanner fails to register app
                //Prompt user to restart device
                if (errorCode == (ScanFailure)2)
                {
                    var notificationService = BC.Mobile.AppContext.Container.Resolve<INotificationManager>();

                    string errorMessage = FeatureResources.GeneralBluetooth + " " + FeatureResources.DiagnosticsError + ". " +
                        FeatureResources.GeneralRestart + " " + FeatureResources.GeneralDevice + ".";

                    notificationService.ShowNotification(new NotificationMessage(
                        errorMessage,
                        BC.Mobile.Utilities.Messaging.Snackbar.NotificationType.Alert,
                        CooldownDuration.TenSecondsInSec,
                        NotificationDuration.FifteenSecondsInMs));

                    _logger.Warn("SCAN_FAILED_APPLICATION_REGISTRATION_FAILED");
                }
            }

            public override void OnScanResult(ScanCallbackType callbackType, ScanResult result)
            {
                base.OnScanResult(callbackType, result);

                try
                {
                    var device = new Device(_adapter, result.Device, null, result.Rssi, result.ScanRecord.GetBytes());
                    _adapter.HandleDiscoveredDevice(device);
                }
                catch (ArgumentException)
                {
                    Trace.Message("Failed to parse scan result and create device");
                }
                catch (Exception e)
                {
                    Trace.Message("Unkown error when creating device based on scan result. Message: " + e.Message);
                }

            }
        }
    }
}



