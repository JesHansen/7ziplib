using System.Configuration;
using System.Threading;

namespace LzmaAlone.Properties
{
    public class Settings : ApplicationSettingsBase
    {
        private static Settings _mValue;

        private static readonly object _mSyncObject = new object();

        public static Settings Value
        {
            get
            {
                if (_mValue == null)
                {
                    Monitor.Enter(_mSyncObject);
                    if (_mValue == null)
                        try
                        {
                            _mValue = new Settings();
                        }
                        finally
                        {
                            Monitor.Exit(_mSyncObject);
                        }
                }

                return _mValue;
            }
        }
    }
}