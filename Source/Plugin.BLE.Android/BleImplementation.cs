// Copyright (C) 2020 Bang & Olufsen A/S - All Rights Reserved
using Android.App;
using Android.Bluetooth;
using Android.Content;
using Android.Content.PM;
using Plugin.BLE.Abstractions;
using Plugin.BLE.Abstractions.Contracts;
using Plugin.BLE.Abstractions.EventArgs;
using Plugin.BLE.BroadcastReceivers;
using Plugin.BLE.Extensions;
using Adapter = Plugin.BLE.Android.Adapter;
using IAdapter = Plugin.BLE.Abstractions.Contracts.IAdapter;

namespace Plugin.BLE
{
    internal class BleImplementation : BleImplementationBase
    {
        private BluetoothManager _bluetoothManager;
        private BluetoothStatusBroadcastReceiver _statusBroadcastReceiver;
        private BondStatusBroadcastReceiver _bondStatusBroadcastReceiver;

        protected override void InitializeNative()
        {
            DefaultTrace.DefaultTraceInit();
            var ctx = Application.Context;
            if (ctx.PackageManager.HasSystemFeature(PackageManager.FeatureBluetoothLe) == false)
            {
                return;
            }

            _statusBroadcastReceiver = new BluetoothStatusBroadcastReceiver(UpdateState);
             ctx.RegisterReceiver(_statusBroadcastReceiver, new IntentFilter(BluetoothAdapter.ActionStateChanged));

             _bondStatusBroadcastReceiver = new BondStatusBroadcastReceiver();
             _bondStatusBroadcastReceiver.BondStateChanged += UpdateBondState;
             ctx.RegisterReceiver(_bondStatusBroadcastReceiver, new IntentFilter(BluetoothAdapter.ActionStateChanged));

            _bluetoothManager = (BluetoothManager)ctx.GetSystemService(Context.BluetoothService);
        }

        public void close()
        {
            _bondStatusBroadcastReceiver.BondStateChanged -= UpdateBondState;
            if (Application.Context != null)
            {
                Application.Context.UnregisterReceiver(_statusBroadcastReceiver);
                Application.Context.UnregisterReceiver(_bondStatusBroadcastReceiver);
            }
        }

        protected override BluetoothState GetInitialStateNative()
        {
            if (_bluetoothManager == null)
                return BluetoothState.Unavailable;

            return _bluetoothManager.Adapter.State.ToBluetoothState();
        }

        protected override IAdapter CreateNativeAdapter()
        {
            return new Adapter(_bluetoothManager);
        }

        private void UpdateState(BluetoothState state)
        {
            State = state;
        }

        private void UpdateBondState(object sender, DeviceBondStateChangedEventArgs state)
        {
        }
    }
}