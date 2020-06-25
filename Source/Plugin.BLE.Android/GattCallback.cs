using System;
using Android.Bluetooth;
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

        public GattCallback(Adapter adapter, Device device)
        {
            _adapter = adapter;
            _device = device;
        }

        public override void OnConnectionStateChange(BluetoothGatt gatt, GattStatus status, ProfileState newState)
        {
            base.OnConnectionStateChange(gatt, status, newState);

            if (!gatt.Device.Address.Equals(_device.BluetoothDevice.Address))
            {
                Trace.Message($"Gatt callback for device {_device.BluetoothDevice.Address} was called for device with address {gatt.Device.Address}. This shoud not happen. Please log an issue.");
                return;
            }

            //ToDo ignore just for me
            Trace.Message($"References of parent device and gatt callback device equal? {ReferenceEquals(_device.BluetoothDevice, gatt.Device).ToString().ToUpper()}");

            Trace.Message($"OnConnectionStateChange: GattStatus: {status}");

            switch (newState)
            {
                // disconnected
                case ProfileState.Disconnected:

                    // Close GATT regardless, else we can accumulate zombie gatts.
                    CloseGattInstances(gatt);

                    if (_device.IsOperationRequested)
                    {
                        Trace.Message("Disconnected by user");

                        //Found so we can remove it
                        _device.IsOperationRequested = false;
                        lock (_adapter.ConnectedDeviceRegistryLock)
                        {
                            _adapter.ConnectedDeviceRegistry.Remove(gatt.Device.Address);
                        }

                        if (status != GattStatus.Success)
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

                    if (status != GattStatus.Success)
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

                        switch (_device.BluetoothDevice.BondState)
                        {
                            case Bond.Bonded:
                            case Bond.None:
                            // Connected to device, now proceed to discover it's services but delay a bit if needed
                                //         int delayWhenBonded = 0;
                                //         if (Build.VERSION.SDK_INT <= Build.VERSION_CODES.N) {
                                //             delayWhenBonded = 1000;
                                //         }
                                //         final int delay = bondstate == BOND_BONDED ? delayWhenBonded : 0;
                                //         discoverServicesRunnable = new Runnable() {
                                //             @Override
                                //             public void run() {
                                //             Log.d(TAG, String.format(Locale.ENGLISH, "discovering services of '%s' with delay of %d ms", getName(), delay));
                                //             boolean result = gatt.discoverServices();
                                //             if (!result) {
                                //             Log.e(TAG, "discoverServices failed to start");
                                //         }
                                //         discoverServicesRunnable = null;
                                // }
                                // };
                                // bleHandler.postDelayed(discoverServicesRunnable, delay);
                                break;
                            case Bond.Bonding:
                            //should wait till bonded
                            // Bonding process in progress, let it complete
                            // Log.i(TAG, "waiting for bonding to complete");
                                break;
                            // case Bond.None:
                            // // gatt.DiscoverServices();
                            default:
                                throw new ArgumentOutOfRangeException();
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
            //ToDO just for me
            Trace.Message($"References of parnet device gatt and callback gatt equal? {ReferenceEquals(_device._gatt, gatt).ToString().ToUpper()}");

            if (!ReferenceEquals(gatt, _device._gatt))
            {
                gatt.Close();
            }

            //cleanup everything else
            _device.CloseGatt();
        }

        public override void OnServicesDiscovered(BluetoothGatt gatt, GattStatus status)
        {
            base.OnServicesDiscovered(gatt, status);
            //gattstatus needs to be updated with even more states/codes
// status == GattStatus.Failure
// {
//     disconect
// }
            Trace.Message("OnServicesDiscovered: {0}", status.ToString());
            // status switch
            // {
            // The device disconnected itself on purpose. For example, because all data has been transferred and there is nothing else to to. You will receive status 19 (GATT_CONN_TERMINATE_PEER_USER).
            //     The connection timed out and the device disconnected itself. In this case you’ll get a status 8 (GATT_CONN_TIMEOUT)
            // There was an low-level error in the communication which led to the loss of the connection. Typically you would receive a status 133 (GATT_ERROR) or a more specific error code if you are lucky!
            // The stack never managed to connect in the first place. In this case you will also receive a status 133 (GATT_ERROR)
            // The connection was lost during service discovery or bonding. In this case you will want to investigate why this happened and perhaps retry the connection.
        //     The first two cases are totally normal and there is nothing else to do than call close() and perhaps do some internal cleanup like disposing of the BluetoothGatt object.
        // In the other cases, you may want to do something like informing other parts of your app or showing something in the UI. If there was a communication error you might be doing something wrong yourself. Alternatively, the device might be doing something wrong. Either way, something to deal with! It is a bit up to you to what extend you want to deal with all possible cases.
        //    https://github.com/weliem/blessed-android/blob/master/blessed/src/main/java/com/welie/blessed/BluetoothPeripheral.java#L234
            // };
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
                //close and try again (hope this is the 133 error code)
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