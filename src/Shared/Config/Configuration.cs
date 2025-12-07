namespace Shared.Config;

using System.Collections.Specialized;
using System.ComponentModel;
using System.Globalization;

//Esta confuguracion le de dos archivos:
//el default(contiene absolutamente todo lo que se desea)
//el que re-escribe (cabios que se desean aplicar/personalizacion
//Esta implementacion es basica posee lo minimo necesitado para una configuracion.

//Este lee formatos donde los comentarios/documentacio 
//se encuentran al principio del archivo y empiezan con # 
//Ignora cada liena que sea comentarios y lineas en blanco 
//De no ser alguna de las previas sera parceada y se leera el Key y el Value
//El key y el value se dividiran por 2 y se le removeran los espacios y tabs
//despues y antes de cada uno y se añadiran a la configuracion global

//Una ves lee el archivo existente, logra la configuracion...
// y lo añade a la configuracion global
//Todos los keys que se encuentren en el archivo default y se repitan...
//en el archivo que re-escribe seran remplazados por el segundo (el que re-escribe)  

//Hay varios metodos para obtener la configuracion
//Fueran a haber Keys que no se encuentren en la configuracion...
//devolvera un valor default (no re-escribe)
//Tambien permite recibir values dee otro tipo (int, float, etc.)
//Fuera a fallar el parcing o el valor no existir devuelve un default
public static class Configuration
{
	private static StringDictionary? appConfiguration;

	private static StringDictionary GetAppConfiguration()
	{
		return appConfiguration == null ?
		appConfiguration = LoadAppConfiguration() : appConfiguration;
	}

	private static StringDictionary LoadAppConfiguration()
	{
		var cfg = new StringDictionary();
		var basePath = Directory.GetCurrentDirectory();
		var deploymentMode = Environment.GetEnvironmentVariable("DEPLOYMENT_MODE") ?? "development";
		var paths = new string[] { "appsettings.cfg", $"appsettings.{deploymentMode}.cfg" };

		foreach (var path in paths)
		{
			var file = Path.Combine(basePath, path);

			if (File.Exists(file))
			{
				var tmp = LoadConfigurationFile(file);

				foreach (string k in tmp.Keys) { cfg[k] = tmp[k]; }
			}
		}

		return cfg;
	}

	public static StringDictionary LoadConfigurationFile(string file)
	{
		string[] lines = File.ReadAllLines(file);

		var cfg = new StringDictionary();

		for (int i = 0; i < lines.Length; i++)
		{
			string line = lines[i].TrimStart();

			if (string.IsNullOrEmpty(line) || line.StartsWith('#')) { continue; }

			var kv = line.Split('=', 2, StringSplitOptions.TrimEntries);

			cfg[kv[0]] = kv[1];
		}

		return cfg;
	}

	public static string? Get(string key)
	{
		return Get(key, null);
	}

	public static string? Get(string key, string? val)
	{
		return Environment.GetEnvironmentVariable(key)
		 ?? GetAppConfiguration()[key] ?? val;
	}

	public static T Get<T>(string key)
	{
		return Get<T>(key, default!);
	}

	public static T Get<T>(string key, T val)
	{
		string? value = Environment.GetEnvironmentVariable(key)
			?? GetAppConfiguration()[key];

		if (string.IsNullOrWhiteSpace(value)) { return val; }

		try
		{
			Type targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);

			if (targetType.IsEnum)
			{
				return (T)Enum.Parse(targetType, value, ignoreCase: true);
			}

			var converter = TypeDescriptor.GetConverter(targetType);

			if (converter != null && converter.CanConvertFrom(typeof(string)))
			{
				return (T)converter.ConvertFromString(null, CultureInfo.InvariantCulture, value)!;
			}

			return (T)Convert.ChangeType(value, targetType, CultureInfo.InvariantCulture);
		}
		catch
		{
			return val;
		}
	}
}