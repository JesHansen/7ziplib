
namespace LzmaAlone.Properties
{
	public class Settings : System.Configuration.ApplicationSettingsBase
	{
		private static Settings _mValue;

		private static object _mSyncObject = new object();

		public static Settings Value
		{
			get
			{
				if (_mValue == null)
				{
					System.Threading.Monitor.Enter(_mSyncObject);
					if (_mValue == null)
					{
						try
						{
							_mValue = new Settings();
						}
						finally
						{
							System.Threading.Monitor.Exit(_mSyncObject);
						}
					}
				}
				return _mValue;
			}
		}
	}
}
