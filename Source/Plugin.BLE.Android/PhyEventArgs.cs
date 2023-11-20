using System;
using Android.Bluetooth;
using Android.Bluetooth.LE;

namespace Plugin.BLE
{
    public class PhyEventArgs : EventArgs
    {
        public ScanSettingsPhy RxPhy { get; set; }

        public ScanSettingsPhy TxPhy { get; set; }

        public GattStatus Status { get; set; }
    }
}