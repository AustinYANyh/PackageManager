using System;
using System.Collections.Generic;

namespace PackageManager.Services
{
    public static class ServiceLocator
    {
        private static readonly Dictionary<Type, object> Services = new Dictionary<Type, object>();

        public static void Register<T>(T instance) where T : class
        {
            Services[typeof(T)] = instance;
        }

        public static T Resolve<T>() where T : class
        {
            return Services.TryGetValue(typeof(T), out var svc) ? (T)svc : null;
        }
    }
}
