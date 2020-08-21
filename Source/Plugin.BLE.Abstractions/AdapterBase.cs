using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Plugin.BLE.Abstractions.Exceptions;
using Plugin.BLE.Abstractions.Utils;

namespace Plugin.BLE.Abstractions
{
    public abstract class AdapterBase : IAdapter
    {
        protected const int MaxConnectionWaitTimeMS = 4_000;
        protected const int MaxScanTimeMS = 5_000;

        private CancellationTokenSource _scanCancellationTokenSource;
        private readonly IList<IDevice> _discoveredDevices;
        private volatile bool _isScanning;
        private Func<IDevice, bool> _currentScanDeviceFilter;

        public event EventHandler<DeviceEventArgs> DeviceAdvertised = delegate { };
        public event EventHandler<DeviceEventArgs> DeviceDiscovered = delegate { };
        public event EventHandler<DeviceEventArgs> DeviceConnected = delegate { };
        public event EventHandler<DeviceEventArgs> DeviceDisconnected = delegate { };
        public event EventHandler<DeviceErrorEventArgs> DeviceConnectionLost = delegate { };
        public event EventHandler<DeviceErrorEventArgs> DeviceConnectionError = delegate { };
        public event EventHandler ScanTimeoutElapsed = delegate { };

        public bool IsScanning
        {
            get { return _isScanning; }
            private set { _isScanning = value; }
        }

        public int ScanTimeout { get; set; } = MaxScanTimeMS;
        public ScanMode ScanMode { get; set; } = ScanMode.LowPower;

        public virtual IList<IDevice> DiscoveredDevices => _discoveredDevices;

        public abstract IList<IDevice> ConnectedDevices { get; }

        protected AdapterBase()
        {
            _discoveredDevices = new List<IDevice>();
        }

        public async Task StartScanningForDevicesAsync(Guid[] serviceUuids = null, Func<IDevice, bool> deviceFilter = null, bool allowDuplicatesKey = false, CancellationToken cancellationToken = default(CancellationToken))
        {
            if (IsScanning)
            {
                Trace.Message("Adapter: Already scanning! Restarting...");
                _scanCancellationTokenSource.Cancel();
                
                //Wait a bit for the previous scan to stop
                await Task.Delay(1000, cancellationToken);
            }

            IsScanning = true;
            serviceUuids = serviceUuids ?? new Guid[0];
            _currentScanDeviceFilter = deviceFilter ?? (d => true);
            _scanCancellationTokenSource = new CancellationTokenSource();

            try
            {
                using (cancellationToken.Register(() => _scanCancellationTokenSource?.Cancel()))
                {
                    await StartScanningForDevicesNativeAsync(serviceUuids, allowDuplicatesKey, _scanCancellationTokenSource.Token);
                    await Task.Delay(ScanTimeout, _scanCancellationTokenSource.Token);
                    Trace.Message("Adapter: Scan timeout has elapsed.");
                    ScanTimeoutElapsed(this, new System.EventArgs());
                }
            }
            catch (TaskCanceledException)
            {

                Trace.Message("Adapter: Scan was cancelled.");
            }
            finally
            {
                CleanupScan();
            }
        }

        public Task StopScanningForDevicesAsync()
        {
            if (_scanCancellationTokenSource != null && !_scanCancellationTokenSource.IsCancellationRequested)
            {
                _scanCancellationTokenSource.Cancel();
            }
            else
            {
                Trace.Message("Adapter: Already cancelled scan.");
            }

            return Task.FromResult(0);
        }

        public Task<IDevice> ConnectAsync(Guid uuid, Func<IDevice, bool> deviceFilter, CancellationToken cancellationToken = default(CancellationToken))
        {
            return ConnectNativeAsync(uuid, deviceFilter, cancellationToken);
        }

        public async Task ConnectToDeviceAsync(IDevice device, ConnectParameters connectParameters = default(ConnectParameters), CancellationToken cancellationToken = default(CancellationToken))
        {
            if (device == null)
                throw new ArgumentNullException(nameof(device));

            if (device.State == DeviceState.Connected)
                return;

            var tryForAWhile = new CancellationTokenSource(TimeSpan.FromSeconds(20));

            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, tryForAWhile.Token))
            {
                var foundDevice = false;
                var tryCount = 0;
                while (cts.Token.IsCancellationRequested == false && foundDevice == false)
                {
                    tryCount++;
                    Trace.Message("Trying to connect, tries {0}", tryCount);
                    try
                    {
                        foundDevice = await TaskBuilder.FromEvent<bool, EventHandler<DeviceEventArgs>, EventHandler<DeviceErrorEventArgs>>(
                            execute: () =>
                            {
                                ConnectToDeviceNativeAsync(device, connectParameters, cts.Token);
                            },

                            getCompleteHandler: (complete, reject) => (sender, args) =>
                            {
                                if (args.Device.Id == device.Id)
                                {
                                    Trace.Message("ConnectToDeviceAsync Connected: {0} {1}", args.Device.Id, args.Device.Name);
                                    complete(true);
                                }
                            },
                            subscribeComplete: handler => DeviceConnected += handler,
                            unsubscribeComplete: handler => DeviceConnected -= handler,

                            getRejectHandler: reject => (sender, args) =>
                            {
                                if (args.Device?.Id == device.Id)
                                {
                                    Trace.Message("ConnectAsync Error: {0} {1}", args.Device?.Id, args.Device?.Name);
                                    reject(new DeviceConnectionException((Guid)args.Device?.Id, args.Device?.Name,
                                        args.ErrorMessage));
                                }
                            },

                            subscribeReject: handler => DeviceConnectionError += handler,
                            unsubscribeReject: handler => DeviceConnectionError -= handler,
                            token: cts.Token);
                    }
                    catch (Exception)
                    {
                        if (cts.Token.IsCancellationRequested)
                        {
                            Trace.Message("Connecting to {0} {1} timed out after {2} tries", device.Id, device.Name, tryCount);
                            throw;
                        }
                    }
                }

                Trace.Message((foundDevice ? "Succeeded" : "Gave up") + " after {0} tries", tryCount);
            }
        }

        public Task DisconnectDeviceAsync(IDevice device)
        {
            if (!ConnectedDevices.Contains(device))
            {
                Trace.Message("Disconnect async: device {0} not in the list of connected devices.", device.Name);
                return Task.FromResult(false);
            }

            return TaskBuilder.FromEvent<bool, EventHandler<DeviceEventArgs>, EventHandler<DeviceErrorEventArgs>>(
               execute: () => DisconnectDeviceNative(device),

               getCompleteHandler: (complete, reject) => ((sender, args) =>
               {
                   if (args.Device.Id == device.Id)
                   {
                       Trace.Message("DisconnectAsync Disconnected: {0} {1}", args.Device.Id, args.Device.Name);
                       complete(true);
                   }
               }),
               subscribeComplete: handler => DeviceDisconnected += handler,
               unsubscribeComplete: handler => DeviceDisconnected -= handler,

               getRejectHandler: reject => ((sender, args) =>
               {
                   if (args.Device.Id == device.Id)
                   {
                       Trace.Message("DisconnectAsync", "Disconnect Error: {0} {1}", args.Device?.Id, args.Device?.Name);
                       reject(new Exception("Disconnect operation exception"));
                   }
               }),
               subscribeReject: handler => DeviceConnectionError += handler,
               unsubscribeReject: handler => DeviceConnectionError -= handler);
        }

        private void CleanupScan()
        {
            Trace.Message("Adapter: Stopping the scan for devices.");
            StopScanNative();

            if (_scanCancellationTokenSource != null)
            {
                _scanCancellationTokenSource.Dispose();
                _scanCancellationTokenSource = null;
            }

            IsScanning = false;
        }

        public void HandleDiscoveredDevice(IDevice device)
        {
            if (!_currentScanDeviceFilter(device))
                return;

            DeviceAdvertised(this, new DeviceEventArgs { Device = device });

            // TODO (sms): check equality implementation of device
            if (_discoveredDevices.Contains(device))
                return;

            _discoveredDevices.Add(device);
            DeviceDiscovered(this, new DeviceEventArgs { Device = device });
        }

        public void HandleConnectedDevice(IDevice device)
        {
            DeviceConnected(this, new DeviceEventArgs { Device = device });
        }

        public void HandleDisconnectedDevice(bool disconnectRequested, IDevice device)
        {
            if (disconnectRequested)
            {
                Trace.Message("DisconnectedPeripheral by user: {0}", device.Name);
                DeviceDisconnected(this, new DeviceEventArgs { Device = device });
            }
            else
            {
                Trace.Message("DisconnectedPeripheral by lost signal: {0}", device.Name);
                DeviceConnectionLost(this, new DeviceErrorEventArgs { Device = device });

                if (DiscoveredDevices.Contains(device))
                {
                    DiscoveredDevices.Remove(device);
                }
            }
        }

        public void HandleConnectionFail(IDevice device, string errorMessage)
        {
            Trace.Message("Failed to connect peripheral {0}: {1}", device.Id, device.Name);
            DeviceConnectionError(this, new DeviceErrorEventArgs
            {
                Device = device,
                ErrorMessage = errorMessage
            });
        }

        protected abstract Task StartScanningForDevicesNativeAsync(Guid[] serviceUuids, bool allowDuplicatesKey, CancellationToken scanCancellationToken);
        protected abstract void StopScanNative();
        protected abstract Task ConnectToDeviceNativeAsync(IDevice device, ConnectParameters connectParameters, CancellationToken cancellationToken);
        protected abstract void DisconnectDeviceNative(IDevice device);
        protected abstract Task<IDevice> ConnectNativeAsync(Guid uuid, Func<IDevice, bool> deviceFilter, CancellationToken cancellationToken = default(CancellationToken));

        public abstract Task<IDevice> ConnectToKnownDeviceAsync(Guid deviceGuid, ConnectParameters connectParameters = default(ConnectParameters), CancellationToken cancellationToken = default(CancellationToken));
        public abstract List<IDevice> GetSystemConnectedOrPairedDevices(Guid[] services = null);
    }
}