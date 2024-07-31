﻿using System;
using Android.Bluetooth;
using Android.Bluetooth.LE;
using Android.Runtime;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Extensions;
using Plugin.BLE.Android.CallbackEventArgs;

namespace Plugin.BLE.Android
{
    public interface IGattCallback
    {
        event EventHandler<ServicesDiscoveredCallbackEventArgs> ServicesDiscovered;
        event EventHandler<CharacteristicReadCallbackEventArgs> CharacteristicValueUpdated;
        event EventHandler<CharacteristicWriteCallbackEventArgs> CharacteristicValueWritten;
        event EventHandler<DescriptorCallbackEventArgs> DescriptorValueWritten;
        event EventHandler<DescriptorCallbackEventArgs> DescriptorValueRead;
        event EventHandler<RssiReadCallbackEventArgs> RemoteRssiRead;
        event EventHandler ConnectionInterrupted;
        event EventHandler<MtuRequestCallbackEventArgs> MtuRequested;

        event EventHandler<PhyEventArgs> OnPhyChange;
    }

    public class GattCallback : BluetoothGattCallback, IGattCallback
    {
        private readonly Adapter _adapter;
        private readonly Device _device;
        public event EventHandler<ServicesDiscoveredCallbackEventArgs> ServicesDiscovered;
        public event EventHandler<CharacteristicReadCallbackEventArgs> CharacteristicValueUpdated;
        public event EventHandler<CharacteristicWriteCallbackEventArgs> CharacteristicValueWritten;
        public event EventHandler<RssiReadCallbackEventArgs> RemoteRssiRead;
        public event EventHandler ConnectionInterrupted;
        public event EventHandler<DescriptorCallbackEventArgs> DescriptorValueWritten;
        public event EventHandler<DescriptorCallbackEventArgs> DescriptorValueRead;
        public event EventHandler<MtuRequestCallbackEventArgs> MtuRequested;
        public event EventHandler OnDisconnected;

        public event EventHandler<PhyEventArgs> OnPhyChange;

        public GattCallback(Adapter adapter, Device device)
        {
            _adapter = adapter;
            _device = device;
        }

        public override void OnPhyRead(BluetoothGatt gatt, [GeneratedEnum] ScanSettingsPhy txPhy, [GeneratedEnum] ScanSettingsPhy rxPhy, [GeneratedEnum] GattStatus status)
        {
            Trace.Message($"[{gatt.Device.Name} PHY read] tx: {txPhy} rx: {rxPhy} status: {status}");
            base.OnPhyRead(gatt, txPhy, rxPhy, status);
        }

        public override void OnPhyUpdate(BluetoothGatt gatt, [GeneratedEnum] ScanSettingsPhy txPhy, [GeneratedEnum] ScanSettingsPhy rxPhy, [GeneratedEnum] GattStatus status)
        {
            Trace.Message($"[{gatt.Device.Name} PHY update] tx: {txPhy} rx: {rxPhy} status: {status}");
            base.OnPhyUpdate(gatt, txPhy, rxPhy, status);
        }

        public override void OnConnectionStateChange(BluetoothGatt gatt, GattStatus status, ProfileState newState)
        {
            base.OnConnectionStateChange(gatt, status, newState);

            if (!gatt.Device.Address.Equals(_device.BluetoothDevice.Address))
            {
                Trace.Message($"Gatt callback for device {_device.BluetoothDevice.Address} was called for device with address {gatt.Device.Address}. This shoud not happen. Please log an issue.");
                return;
            }

            Trace.Message($"New connection state for {gatt.Device.Name}: {newState}");

            Trace.Message($"OnConnectionStateChange: GattStatus: {status}");

            switch (newState)
            {
                // disconnected
                case ProfileState.Disconnected:

                    // Close GATT regardless, else we can accumulate zombie gatts.
                    CloseGattInstances(gatt);

                    //nasty hack until we update the BLE Plugin
                    if (_device.IsOperationRequested || status == GattStatus.InsufficientAuthentication)
                    {
                        Trace.Message("Disconnected by user or GattStatus.InsufficientAuthentication");

                        //Found so we can remove it
                        _device.IsOperationRequested = false;
                        lock (_adapter.ConnectedDeviceRegistryLock)
                        {
                            _adapter.ConnectedDeviceRegistry.Remove(gatt.Device.Address);
                        }

                        if (status != GattStatus.Success || status == GattStatus.InsufficientAuthentication)
                        {
                            // The above error event handles the case where the error happened during a Connect call, which will close out any waiting asyncs.
                            // Android > 5.0 uses this switch branch when an error occurs during connect
                            Trace.Message($"Error while connecting '{_device.Name}'. Not raising disconnect event.");
                            _adapter.HandleConnectionFail(_device, $"GattCallback error: {status}");
                        }
                        else
                        {
                            //we already hadled device error so no need th raise disconnect event(happens when device not in range)
                            _adapter.HandleDisconnectedDevice(true, _device);
                            OnDisconnected?.Invoke(this, EventArgs.Empty);
                        }

                        _device.Dispose();
                        break;
                    }

                    //connection must have been lost, because the callback was not triggered by calling disconnect
                    Trace.Message($"Disconnected '{_device.Name}' by lost connection");

                    lock (_adapter.ConnectedDeviceRegistryLock)
                    {
                        _adapter.ConnectedDeviceRegistry.Remove(gatt.Device.Address);
                    }

                    _adapter.HandleDisconnectedDevice(false, _device);
                    OnDisconnected?.Invoke(this, EventArgs.Empty);

                    // inform pending tasks
                    ConnectionInterrupted?.Invoke(this, EventArgs.Empty);
                    break;
                // connecting
                case ProfileState.Connecting:
                    Trace.Message("Connecting");
                    break;
                // connected
                case ProfileState.Connected:
                    Trace.Message("Connected");

                    //Check if the operation was requested by the user
                    if (_device.IsOperationRequested)
                    {
                        _device.Update(gatt.Device, gatt);

                        //Found so we can remove it
                        _device.IsOperationRequested = false;
                    }
                    else
                    {
                        //ToDo explore this
                        //only for on auto-reconnect (device is not in operation registry)
                        _device.Update(gatt.Device, gatt);
                    }

                    if (status != GattStatus.Success || status == GattStatus.InsufficientAuthentication)
                    {
                        // The above error event handles the case where the error happened during a Connect call, which will close out any waiting asyncs.
                        // Android <= 4.4 uses this switch branch when an error occurs during connect
                        Trace.Message($"Error while connecting '{_device.Name}'. GattStatus: {status}. ");
                        _adapter.HandleConnectionFail(_device, $"GattCallback error: {status}");

                        CloseGattInstances(gatt);
                        _device.Dispose();
                    }
                    else
                    {
                        if (gatt.Device.Address == null || _device == null)
                        {
                            Trace.Info($"Address or device is null address is: {gatt.Device.Address} device is: {_device} GattStatus is: {status}");
                            return;
                        }

                        lock (_adapter.ConnectedDeviceRegistryLock)
                        {
                            _adapter.ConnectedDeviceRegistry[gatt.Device.Address] = _device;
                        }
                        _adapter.HandleConnectedDevice(_device);
                    }

                    break;
                // disconnecting
                case ProfileState.Disconnecting:
                    Trace.Message("Disconnecting");
                    break;
            }
        }

        private void CloseGattInstances(BluetoothGatt gatt)
        {
            Trace.Message("Explicitly disconnect gatt instance");

            if (gatt != null)
            {
                gatt.Disconnect();
                gatt.Close();
            }

            //cleanup everything else
            _device.CloseGatt();
        }

        public override void OnServicesDiscovered(BluetoothGatt gatt, GattStatus status)
        {
            base.OnServicesDiscovered(gatt, status);

            Trace.Message("OnServicesDiscovered: {0}", status.ToString());

            ServicesDiscovered?.Invoke(this, new ServicesDiscoveredCallbackEventArgs());
        }

        public override void OnCharacteristicRead(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, GattStatus status)
        {
            base.OnCharacteristicRead(gatt, characteristic, status);

            Trace.Message("OnCharacteristicRead: value {0}; status {1}", characteristic.GetValue().ToHexString(), status);

            CharacteristicValueUpdated?.Invoke(this, new CharacteristicReadCallbackEventArgs(characteristic));
        }

        public override void OnCharacteristicChanged(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic)
        {
            base.OnCharacteristicChanged(gatt, characteristic);

            CharacteristicValueUpdated?.Invoke(this, new CharacteristicReadCallbackEventArgs(characteristic));
        }

        public override void OnCharacteristicWrite(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, GattStatus status)
        {
            base.OnCharacteristicWrite(gatt, characteristic, status);

            //Trace.Message("OnCharacteristicWrite: value {0} status {1}", characteristic.GetValue().ToHexString(), status);

            CharacteristicValueWritten?.Invoke(this, new CharacteristicWriteCallbackEventArgs(characteristic, GetExceptionFromGattStatus(status)));
        }

        public override void OnReliableWriteCompleted(BluetoothGatt gatt, GattStatus status)
        {
            base.OnReliableWriteCompleted(gatt, status);

            Trace.Message("OnReliableWriteCompleted: {0}", status);
        }

        public override void OnMtuChanged(BluetoothGatt gatt, int mtu, GattStatus status)
        {
            base.OnMtuChanged(gatt, mtu, status);

            Trace.Message("OnMtuChanged to value: {0}", mtu);

            MtuRequested?.Invoke(this, new MtuRequestCallbackEventArgs(GetExceptionFromGattStatus(status), mtu));
        }

        public override void OnReadRemoteRssi(BluetoothGatt gatt, int rssi, GattStatus status)
        {
            base.OnReadRemoteRssi(gatt, rssi, status);

            Trace.Message("OnReadRemoteRssi: device {0} status {1} value {2}", gatt.Device.Name, status, rssi);

            RemoteRssiRead?.Invoke(this, new RssiReadCallbackEventArgs(GetExceptionFromGattStatus(status), rssi));
        }

        public override void OnDescriptorWrite(BluetoothGatt gatt, BluetoothGattDescriptor descriptor, GattStatus status)
        {
            base.OnDescriptorWrite(gatt, descriptor, status);

            Trace.Message("OnDescriptorWrite: {0}", descriptor.GetValue()?.ToHexString());

            DescriptorValueWritten?.Invoke(this, new DescriptorCallbackEventArgs(descriptor, GetExceptionFromGattStatus(status)));
        }

        public override void OnDescriptorRead(BluetoothGatt gatt, BluetoothGattDescriptor descriptor, GattStatus status)
        {
            base.OnDescriptorRead(gatt, descriptor, status);

            Trace.Message("OnDescriptorRead: {0}", descriptor.GetValue()?.ToHexString());

            DescriptorValueRead?.Invoke(this, new DescriptorCallbackEventArgs(descriptor, GetExceptionFromGattStatus(status)));
        }

        private Exception GetExceptionFromGattStatus(GattStatus status)
        {
            Exception exception = null;
            switch (status)
            {
                case GattStatus.Failure:
                case GattStatus.InsufficientAuthentication:
                case GattStatus.InsufficientEncryption:
                case GattStatus.InvalidAttributeLength:
                case GattStatus.InvalidOffset:
                case GattStatus.ReadNotPermitted:
                case GattStatus.RequestNotSupported:
                case GattStatus.WriteNotPermitted:
                    exception = new Exception(status.ToString());
                    break;
                case GattStatus.Success:
                    break;
            }

            return exception;
        }
    }
}