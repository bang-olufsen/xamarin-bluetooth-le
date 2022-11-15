using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
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
        private volatile bool _isScanning;
        private Func<IDevice, bool> _currentScanDeviceFilter;

        public event EventHandler<DeviceEventArgs> DeviceAdvertised;
        public event EventHandler<DeviceEventArgs> DeviceDiscovered;
        public event EventHandler<DeviceEventArgs> DeviceConnected;
        public event EventHandler<DeviceEventArgs> DeviceDisconnected;
        public event EventHandler<DeviceErrorEventArgs> DeviceConnectionLost;
        public event EventHandler<DeviceErrorEventArgs> DeviceConnectionError;
        public event EventHandler ScanTimeoutElapsed;

        public bool IsScanning
        {
            get => _isScanning;
            private set => _isScanning = value;
        }

        public int ScanTimeout { get; set; } = MaxScanTimeMS;
        public ScanMode ScanMode { get; set; } = ScanMode.LowPower;


        /// <summary>
        /// Scan match mode defines how agressively we look for adverts
        /// </summary>
        public ScanMatchMode ScanMatchMode { get; set; } = ScanMatchMode.STICKY;


        protected ConcurrentDictionary<Guid, IDevice> DiscoveredDevicesRegistry { get; } = new ConcurrentDictionary<Guid, IDevice>();

        public virtual IReadOnlyList<IDevice> DiscoveredDevices => DiscoveredDevicesRegistry.Values.ToList();

        /// <summary>
        /// Used to store all connected devices
        /// </summary>
        public ConcurrentDictionary<string, IDevice> ConnectedDeviceRegistry { get; } = new ConcurrentDictionary<string, IDevice>();

        public IReadOnlyList<IDevice> ConnectedDevices => ConnectedDeviceRegistry.Values.ToList();

        public async Task StartScanningForDevicesAsync(ScanFilterOptions scanFilterOptions,
            Func<IDevice, bool> deviceFilter = null,
            bool allowDuplicatesKey = false,
            CancellationToken cancellationToken = default)
        {
            if (IsScanning)
            {
                Trace.Message("Adapter: Already scanning! Restarting...");
                _scanCancellationTokenSource.Cancel();

                //Wait a bit for the previous scan to stop
                await Task.Delay(1000, cancellationToken);
            }

            IsScanning = true;
            _currentScanDeviceFilter = deviceFilter ?? (d => true);
            _scanCancellationTokenSource = new CancellationTokenSource();

            try
            {
                DiscoveredDevicesRegistry.Clear();

                using (cancellationToken.Register(() => _scanCancellationTokenSource?.Cancel()))
                {
                    await StartScanningForDevicesNativeAsync(scanFilterOptions, allowDuplicatesKey, _scanCancellationTokenSource.Token);
                    await Task.Delay(ScanTimeout, _scanCancellationTokenSource.Token);
                    Trace.Message("Adapter: Scan timeout has elapsed.");
                    ScanTimeoutElapsed?.Invoke(this, new System.EventArgs());
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

        public async Task StartScanningForDevicesAsync(Guid[] serviceUuids, Func<IDevice, bool> deviceFilter = null, bool allowDuplicatesKey = false,
            CancellationToken cancellationToken = default)
        {
            await StartScanningForDevicesAsync(new ScanFilterOptions { ServiceUuids = serviceUuids }, deviceFilter, allowDuplicatesKey, cancellationToken);
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

        public async Task ConnectToDeviceAsync(IDevice device, ConnectParameters connectParameters = default, CancellationToken cancellationToken = default)
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
            if (_currentScanDeviceFilter != null && !_currentScanDeviceFilter(device))
                return;

            DeviceAdvertised?.Invoke(this, new DeviceEventArgs { Device = device });

            // TODO (sms): check equality implementation of device
            if (DiscoveredDevicesRegistry.ContainsKey(device.Id))
                return;

            DiscoveredDevicesRegistry[device.Id] = device;
            DeviceDiscovered?.Invoke(this, new DeviceEventArgs { Device = device });
        }

        public void HandleConnectedDevice(IDevice device)
        {
            DeviceConnected?.Invoke(this, new DeviceEventArgs { Device = device });
        }

        public void HandleDisconnectedDevice(bool disconnectRequested, IDevice device)
        {
            if (disconnectRequested)
            {
                Trace.Message("DisconnectedPeripheral by user: {0}", device.Name);
                DeviceDisconnected?.Invoke(this, new DeviceEventArgs { Device = device });
            }
            else
            {
                Trace.Message("DisconnectedPeripheral by lost signal: {0}", device.Name);
                DeviceConnectionLost?.Invoke(this, new DeviceErrorEventArgs { Device = device });

                if (DiscoveredDevicesRegistry.TryRemove(device.Id, out _))
                {
                    Trace.Message("Removed device from discovered devices list: {0}", device.Name);
                }
            }
        }

        public void HandleConnectionFail(IDevice device, string errorMessage)
        {
            Trace.Message("Failed to connect peripheral {0}: {1}", device.Id, device.Name);
            DeviceConnectionError?.Invoke(this, new DeviceErrorEventArgs
            {
                Device = device,
                ErrorMessage = errorMessage
            });
        }

        protected abstract Task StartScanningForDevicesNativeAsync(ScanFilterOptions scanFilterOptions, bool allowDuplicatesKey, CancellationToken scanCancellationToken);
        protected abstract void StopScanNative();
        protected abstract Task ConnectToDeviceNativeAsync(IDevice device, ConnectParameters connectParameters, CancellationToken cancellationToken);
        protected abstract void DisconnectDeviceNative(IDevice device);
        protected abstract Task<IDevice> ConnectNativeAsync(Guid uuid, Func<IDevice, bool> deviceFilter, CancellationToken cancellationToken = default(CancellationToken));

        public abstract Task<IDevice> ConnectToKnownDeviceAsync(Guid deviceGuid, ConnectParameters connectParameters = default, CancellationToken cancellationToken = default);
        public abstract IReadOnlyList<IDevice> GetSystemConnectedOrPairedDevices(Guid[] services = null);
        public abstract IReadOnlyList<IDevice> GetKnownDevicesByIds(Guid[] ids);
    }
}
