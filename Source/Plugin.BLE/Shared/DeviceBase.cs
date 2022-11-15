﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Plugin.BLE.Abstractions.Contracts;

namespace Plugin.BLE.Abstractions
{
    public interface ICancellationMaster
    {
        CancellationTokenSource TokenSource { get; set; }
    }

    public static class ICancellationMasterExtensions
    {
        public static CancellationTokenSource GetCombinedSource(this ICancellationMaster cancellationMaster, CancellationToken token)
        {
            return CancellationTokenSource.CreateLinkedTokenSource(cancellationMaster.TokenSource.Token, token);
        }

        public static void CancelEverything(this ICancellationMaster cancellationMaster)
        {
            if (cancellationMaster.TokenSource != null)
            {
                //Cleanup can happen from many threads. Avoid disposed exceptions
                lock (cancellationMaster.TokenSource)
                {
                    cancellationMaster.TokenSource?.Cancel();
                    cancellationMaster.TokenSource?.Dispose();
                    cancellationMaster.TokenSource = null;
                }
            }
        }

        public static void CancelEverythingAndReInitialize(this ICancellationMaster cancellationMaster)
        {
            cancellationMaster.CancelEverything();
            cancellationMaster.TokenSource = new CancellationTokenSource();
        }
    }

    public abstract class DeviceBase<TNativeDevice> : IDevice, ICancellationMaster
    {
        private object _servicesLock = new object();
        protected readonly IAdapter Adapter;
        private readonly List<IService> KnownServices = new List<IService>();
        public Guid Id { get; protected set; }
        public string Name { get; protected set; }
        public int Rssi { get; protected set; }
        public DeviceState State => GetState();
        public IReadOnlyList<AdvertisementRecord> AdvertisementRecords { get; protected set; }
        public TNativeDevice NativeDevice { get; protected set; }
        CancellationTokenSource ICancellationMaster.TokenSource { get; set; } = new CancellationTokenSource();
        object IDevice.NativeDevice => NativeDevice;

        protected DeviceBase(IAdapter adapter, TNativeDevice nativeDevice)
        {
            Adapter = adapter;
            NativeDevice = nativeDevice;
        }

        public async Task<IReadOnlyList<IService>> GetServicesAsync(CancellationToken cancellationToken = default)
        {
            lock (KnownServices)
            {
                if (KnownServices.Any())
                {
                    return KnownServices.ToArray();
                }
            }

            using (var source = this.GetCombinedSource(cancellationToken))
            {
                var services = await GetServicesNativeAsync();

                lock (KnownServices)
                {
                    if (services != null)
                        KnownServices.AddRange(services);

                    return KnownServices.ToArray();
                }
            }
        }

        public async Task<IService> GetServiceAsync(Guid id, CancellationToken cancellationToken = default)
        {
            var services = await GetServicesAsync(cancellationToken);

            var hasIdenticalServiceIds = services?.Count(x => x?.Id == id) > 1;
            if (hasIdenticalServiceIds)
            {
                Trace.Message("NOTICE: Identical services found, first service will be returned");
            }

            return services.ToList().FirstOrDefault(x => x.Id == id);
        }

        public async Task<int> RequestMtuAsync(int requestValue)
        {
            return await RequestMtuNativeAsync(requestValue);
        }

        public bool UpdateConnectionInterval(ConnectionInterval interval)
        {
            return UpdateConnectionIntervalNative(interval);
        }

        public abstract Task<bool> UpdateRssiAsync();
        protected abstract DeviceState GetState();
        protected abstract Task<IReadOnlyList<IService>> GetServicesNativeAsync();
        protected abstract Task<IService> GetServiceNativeAsync(Guid id);
        protected abstract Task<int> RequestMtuNativeAsync(int requestValue);
        protected abstract bool UpdateConnectionIntervalNative(ConnectionInterval interval);

        public override string ToString()
        {
            return Name;
        }

        public virtual void Dispose()
        {
            Adapter.DisconnectDeviceAsync(this);
        }

        public void ClearServices()
        {
            this.CancelEverythingAndReInitialize();

            lock (_servicesLock)
            {
                if (KnownServices != null)
                {
                    foreach (var service in KnownServices)
                    {
                        try
                        {
                            service.Dispose();
                        }
                        catch (Exception ex)
                        {
                            Trace.Message("Exception while cleanup of service: {0}", ex.Message);
                        }
                    }

                    KnownServices.Clear();
                }
            }
        }

        public override bool Equals(object other)
        {
            if (other == null)
            {
                return false;
            }

            if (other.GetType() != GetType())
            {
                return false;
            }

            var otherDeviceBase = (DeviceBase<TNativeDevice>)other;
            return Id == otherDeviceBase.Id;
        }

        public override int GetHashCode()
        {
            return Id.GetHashCode();
        }
    }
}