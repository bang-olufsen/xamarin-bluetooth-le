using System;
using Android.Bluetooth;
using Android.Content;
using Android.OS;
using Plugin.BLE.Abstractions.EventArgs;
using Plugin.BLE.Android;
using Plugin.BLE.Extensions;

namespace Plugin.BLE.BroadcastReceivers
{
    public class BondStatusBroadcastReceiver : BroadcastReceiver
    {
	    private readonly Adapter _broadcastAdapter;

        public event EventHandler<DeviceBondStateChangedEventArgs> BondStateChanged;

        public BondStatusBroadcastReceiver(Adapter adapter)
        {
            _broadcastAdapter = adapter;
        }

        public override void OnReceive(Context context, Intent intent)
        {
            if (BondStateChanged == null)
            {
	            return;
            }

            var extraBondState = (Bond)intent.GetIntExtra(BluetoothDevice.ExtraBondState, (int)Bond.None);

            BluetoothDevice bluetoothDevice;

#if NET6_0_OR_GREATER
            if (OperatingSystem.IsAndroidVersionAtLeast(33))
#else
            if (Build.VERSION.SdkInt >= BuildVersionCodes.P)
#endif
            {
	            bluetoothDevice = (BluetoothDevice)intent.GetParcelableExtra(BluetoothDevice.ExtraDevice);
            }
            else
            {
	            bluetoothDevice = (BluetoothDevice)intent.GetParcelableExtra(BluetoothDevice.ExtraDevice);
            }

            var device = new Device(_broadcastAdapter, bluetoothDevice, null);

            var address = bluetoothDevice?.Address ?? string.Empty;

            var bondState = extraBondState.FromNative();
            BondStateChanged(this, new DeviceBondStateChangedEventArgs() { Address = address, Device = device, State = bondState });
        }
    }
}
