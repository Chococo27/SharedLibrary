namespace Shared.Http;

using System;
using System.Collections;
using System.Collections.Specialized;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Web;
using System.Xml.Linq;
using Shared.Config;

public static class HttpUtils
{
	//Structured Logging Middleware
	//Se verifica que el id esta dentro de los props de no ser haci se genera uno...
	//utilizando una libreria que posee C# y se toma la fecha en formato UTC
	//Se extrae el metodo request, el path del URL, el IP address...
	//y se asigna a la respuesta un custom header
	//Luego de capturar la info de entrada se intenta llamar al proximo middleware del pipeline
	//Luego de haber corrido todos los middlewares se coge el tiempo que tomo...
	//para satisfacer el request y lo calcula en nano segundos
	//Se envia o estructura toda la data en un Json file con los campos
	//(start time, request ID, el metodo, URL, remoto, respuesta, tipo de contenido (Json, HTML, etc)...
	//largo y cuanto duro)
	//Finalmente se escribe la respuesta en pantalla
	public static async Task StructuredLogging(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
	{
		//Pre-processing
		var requestId = props["req.id"]?.ToString() ?? Guid.NewGuid().ToString("n").Substring(0, 12);
		var startUtc = DateTime.UtcNow;
		var method = req.HttpMethod ?? "UNKNOWN";
		var url = req.Url!.OriginalString ?? req.Url!.ToString();
		var remote = req.RemoteEndPoint.ToString() ?? "unknown";

		res.Headers["X-Request-Id"] = requestId;

		try
		{
			await next();
		}

		//Post-processing
		finally
		{
			var duration = (DateTime.UtcNow - startUtc).TotalNanoseconds;

			var record = new
			{
				timestamp = startUtc.ToString("o"),
				requestId,
				method,
				url,
				remote,
				statusCode = res.StatusCode,
				contentType = res.ContentType,
				contentLength = res.ContentLength64,
				duration
			};
			Console.WriteLine(JsonSerializer.Serialize(record, JsonSerializerOptions.Web));
		}
	}

	//Centralized Error Handling Middleware 
	//Se encarga de manejar errores centralizados
	//Llama al proximo middleware del pipeline y fueran a alguno de ellos...
	//cometer un error este middleware captura dicho error  y devuelve una respuesta...
	//con un estatus code apropiado (clientes reciben aviso de que un error ocurrio,
	//developers reciben todo los detalles del error)
	public static async Task CentralizedErrorHandling(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
	{
		try
		{
			await next();
		}
		catch (Exception e)
		{
			int code = (int)HttpStatusCode.InternalServerError;
			string message = Environment.GetEnvironmentVariable("DEPLOYMENT_MODE") == "production" ? "An unexpected error occurred." : e.ToString();
			await SendResponse(req, res, props, code, message, "text/plain");
		}
	}

	//Default Response
	//Middleware que provee una respuesta generica (User friendly)
	//Permite que cada vez que un usuario o cliente trate de accesder una ruta que...
	//no esta registrada en nuestro router muestre un mensaje de error
	public static async Task DefaultResponse(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
	{
		await next();

		if (res.StatusCode == HttpRouter.RESPONSE_NOT_SENT)
		{
			res.StatusCode = (int)HttpStatusCode.NotFound;
			res.Close();
		}
	}

	//Static File Serving Middleware
	//Middleware responsable de servir documentos estaticos
	//Usa la configuracion y verifica cual es el root directory donde va a buscar
	//los archivos estaticos que va a compartir
	//si no encuentra una propiedad dentro de configuration asume que es el current directory
	//Convierte todo ese directorio en un path y si el archivo existe lo lee,...
	//envia una respuesta de Okay y dependiendo de la extension del archivo...
	//determina el content type y el length basado en el archivo y luego se copia el...
	//archivo directamente al stream de responce y se cierra el archivo y llama al proximo middleware
	public static async Task ServeStaticFiles(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
	{
		string rootDir = Configuration.Get("static.dir", Directory.GetCurrentDirectory())!;
		string urlPath = req.Url!.AbsolutePath.TrimStart('/');
		string filePath = Path.Combine(rootDir, urlPath.Replace('/',
			Path.DirectorySeparatorChar));

		if (File.Exists(filePath))
		{
			using var fs = File.OpenRead(filePath);
			res.StatusCode = (int)HttpStatusCode.OK;
			res.ContentType = GetMimeType(filePath);
			res.ContentLength64 = fs.Length;
			await fs.CopyToAsync(res.OutputStream);
			res.Close();
		}

		await next();
	}

	//Machea las extenciones de los archivos                            
	//Luego se pone en minuscula y se devuelve tipo al res.ContentType
	private static string GetMimeType(string filePath)
	{
		string ext = Path.GetExtension(filePath).ToLowerInvariant();
		return ext switch
		{
			".html" => "text/html; charset=utf-8",
			".htm" => "text/html; charset=utf-8",
			".css" => "text/css",
			".js" => "application/javascript",
			".json" => "application/json",
			".png" => "image/png",
			".jpg" => "image/jpeg",
			".jpeg" => "image/jpeg",
			".gif" => "image/gif",
			".svg" => "image/svg+xml",
			".ico" => "image/x-icon",
			".txt" => "text/plain; charset=utf-8",
			_ => "application/octet-stream"
		};
	}

	//CORS Headers Middleware (Cross Origin Resource Sharing)
	//Son headers que se incluyen en la respuesta para dejarle saber la browser que...
	//la persona merece o es permitida acceso a la informacion
	//Si NO esta en modo de produccion te da acceso a todo
	//De estar en modo de produccion la seguridad es mas extricta...
	//y hay una lista de IP's y puertos permitidos
	//Si no posees origin y el metodo es OPTIONS esta haciendo un preflight
	//Finalmente llama al siguiente middleware
	public static async Task AddResponseCorsHeaders(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
	{
		bool isProductionMode = Environment.GetEnvironmentVariable("DEPLOYMENT_MODE") == "Production";
		string? origin = req.Headers["Origin"];

		if (!string.IsNullOrEmpty(origin))
		{
			if (!isProductionMode)
			{
				// Allow everything during development 
				res.AddHeader("Access-Control-Allow-Origin", origin);
				res.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");
				res.AddHeader("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
				res.AddHeader("Access-Control-Allow-Credentials", "true");
			}
			else
			{
				string[] allowedOrigins = Configuration.Get("allowed.origins", string.Empty)!
					.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

				if (allowedOrigins.Contains(origin, StringComparer.OrdinalIgnoreCase))
				{
					res.AddHeader("Access-Control-Allow-Origin", origin);
					res.AddHeader("Access-Control-Allow-Headers", "Content-Type, Authorization");
					res.AddHeader("Access-Control-Allow-Methods", "GET, POST, PUT, DELETE, OPTIONS");
					res.AddHeader("Access-Control-Allow-Credentials", "true");
				}
			}
		}
		if (req.HttpMethod.Equals("OPTIONS", StringComparison.OrdinalIgnoreCase))
		{
			res.StatusCode = (int)HttpStatusCode.NoContent;
			res.OutputStream.Close();
		}
		await next();
	}

	//URL Parsing Middleware (redundante)
	//Crea un mapping de todas las partes
	//Http listener request ya hace esto (Propositos educativos)
	public static NameValueCollection ParseUrl(string url)
	{
		int i = -1;
		var (scheme, apqf) = (i = url.IndexOf("://")) >= 0 ? (url.Substring(0, i), url.Substring(i + 3)) : ("", url);
		var (auth, pqf) = (i = apqf.IndexOf("/")) >= 0 ? (apqf.Substring(0, i), apqf.Substring(i)) : (apqf, "");
		var (up, hp) = (i = auth.IndexOf("@")) >= 0 ? (auth.Substring(0, i), auth.Substring(i + 1)) : ("", auth);
		var (user, pass) = (i = up.IndexOf(":")) >= 0 ? (up.Substring(0, i), up.Substring(i + 1)) : (up, "");
		var (host, port) = (i = hp.IndexOf(":")) >= 0 ? (hp.Substring(0, i), hp.Substring(i + 1)) : (hp, "");
		var (pq, fragment) = (i = pqf.IndexOf("#")) >= 0 ? (pqf.Substring(0, i), pqf.Substring(i + 1)) : (pqf, "");
		var (path, query) = (i = pq.IndexOf("?")) >= 0 ? (pq.Substring(0, i), pq.Substring(i + 1)) : (pq, "");
		var parts = new NameValueCollection();

		// https://john:abc123@site.com:8080/api/v1/users/3?q=0&active=true#bio 
		// scheme://user:pass@host:port/path?query#fragment 
		// Splits:1     4    3    5    2    7     6 

		// 1 scheme                     user:pass@host:port/path?query#fragment 
		// 2 user:pass@host:port (auth) /path?query#fragment 
		// 3 user:pass                  host:port 
		// 4 user                       pass 
		// 5 host                       port 
		// 6 /path?query                fragment 
		// 7 /path                      query 

		parts["scheme"] = scheme;     // https 
		parts["auth"] = auth;         // john:abc123@site.com:8080 
		parts["user"] = user;         // john 
		parts["pass"] = pass;         // abc123 
		parts["host"] = host;         // site.com 
		parts["port"] = port;         // 8080 
		parts["path"] = path;         // /api/vi/users/3 
		parts["query"] = query;       // q=0&active=true 
		parts["fragment"] = fragment; // bio 

		return parts;
	}

	//Se crea este middleware que cuando llega el request parsearia ese URL y pondria...
	//el mapping bajo los props "req.url" y llamaria al proximo middleware
	public static async Task ParseRequestUrl(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
	{
		props["req.url"] = ParseUrl(req.Url!.OriginalString);
		await next();
	}

	//Query String 
	//Parsea el query string
	public static NameValueCollection ParseQueryString(string text, string duplicateSeparator = ",")
	{
		if (text.StartsWith('?')) { text = text.Substring(1); }
		return ParseFormData(text, duplicateSeparator);
	}

	//Middleware opcinal que recoge el request y lo parsea
	public static async Task ParseRequestQueryString(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
	{
		var url = (NameValueCollection?)props["req.url"];
		props["req.query"] = ParseQueryString(url?["query"] ?? req.Url!.Query);
		await next();
	}

	//Body Parsing Middleware 
	//Parsea FormData
	public static NameValueCollection ParseFormData(string text, string duplicateSeparator = ",")
	{
		var result = new NameValueCollection();
		var pairs = text.Split('&', StringSplitOptions.RemoveEmptyEntries);
		foreach (var pair in pairs)
		{
			var kv = pair.Split('=', 2, StringSplitOptions.None);
			var key = HttpUtility.UrlDecode(kv[0]);
			var value = kv.Length > 1 ? HttpUtility.UrlDecode(kv[1]) : string.Empty;
			var oldValue = result[key];
			result[key] = oldValue == null
			? value : oldValue + duplicateSeparator + value;
		}
		return result;
	}

	//Middleware que lee el body como string y parsea dicho string como formdata...
	//y mete ese nave value collection en props "req.form"
	public static async Task ReadRequestBodyAsForm(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
	{
		using StreamReader sr = new StreamReader(req.InputStream, Encoding.UTF8);
		string formData = await sr.ReadToEndAsync();
		props["req.form"] = ParseFormData(formData);
		await next();
	}

	//Blob
	//Copia el contenido como un arreglo de bytes y lo guarda bajo request blob
	public static async Task ReadRequestBodyAsBlob(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
	{
		using var ms = new MemoryStream();
		await req.InputStream.CopyToAsync(ms);
		props["req.blob"] = ms.ToArray();
		await next();
	}

	//Text
	//Lee el string completo de principio a fin, el paylod del request y...
	//lo guarda en props"req.text"
	public static async Task ReadRequestBodyAsText(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
	{
		var encoding = req.ContentEncoding ?? Encoding.UTF8;
		using StreamReader sr = new StreamReader(req.InputStream, encoding);
		props["req.text"] = await sr.ReadToEndAsync();

		await next();
	}

	//JSON
	//Lee el request body como JsonNode.ParseAsync y luego castea a un Json object
	//luego lo guarda en props "req.json"
	public static async Task ReadRequestBodyAsJson(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
	{
		props["req.json"] = (await JsonNode.ParseAsync(req.InputStream))!.AsObject();

		await next();
	}

	//XML
	//Lee el request body como XML y lo guarda en props "req.xml"
	public static async Task ReadRequestBodyAsXml(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
	{
		props["req.xml"] = await XDocument.LoadAsync(req.InputStream,
			LoadOptions.None, CancellationToken.None);

		await next();
	}

	//Detecting Content Type 
	//Determina si el texto es de tipo Json, Html, Xml o plain
	public static string DetectContentType(string text)
	{
		string s = text.TrimStart();
		if (s.StartsWith("{") || s.StartsWith("["))
		{
			return "application/json";
		}
		else if (s.StartsWith("<!DOCTYPE html", StringComparison.OrdinalIgnoreCase) ||
		s.StartsWith("<html", StringComparison.OrdinalIgnoreCase))
		{
			return "text/html";
		}
		else if (s.StartsWith("<", StringComparison.Ordinal))
		{
			return "application/xml";
		}
		else
		{
			return "text/plain";
		}
	}

	//200 OK Responses 
	//LLama a otra funcion que recibe los parametros con el contenido vacio...
	//y el content type explicitamente puesto a text
	public static async Task SendOkResponse(HttpListenerRequest req, HttpListenerResponse res, Hashtable props)
	{
		await SendOkResponse(req, res, props, string.Empty, "text/plain");
	}

	//Esta recibe el contenido y envia/invoca la funcion de send OK respons...
	//pero con contenido no vacion y el dato lo intenta detectar
	public static async Task SendOkResponse(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, string content)
	{
		await SendOkResponse(req, res, props, content, DetectContentType(content));
	}

	//Llama a la funcion de responce generica con su status code especifico
	public static async Task SendOkResponse(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, string content, string contentType)
	{
		await SendResponse(req, res, props, (int)HttpStatusCode.OK,
			content, contentType);
	}

	//404 Not Found Responses
	//LLama a otra funcion que recibe los parametros con el contenido vacio...
	//y el content type explicitamente puesto a text
	public static async Task SendNotFoundResponse(HttpListenerRequest req, HttpListenerResponse res, Hashtable props)
	{
		await SendNotFoundResponse(req, res, props, string.Empty, "text/plain");
	}

	////Esta recibe el contenido y envia/invoca la funcion de send Not found respons...
	//pero con contenido no vacion y el dato lo intenta detectar
	public static async Task SendNotFoundResponse(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, string content)
	{
		await SendNotFoundResponse(req, res, props, content, DetectContentType(content));
	}

	//Llama a la funcion de responce not found generica con su status code especifico
	public static async Task SendNotFoundResponse(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, string content, string contentType)
	{
		await SendResponse(req, res, props, (int)HttpStatusCode.NotFound,
			content, contentType);
	}

	//Other Status Responses 
	//Listado de utilidades para enviar respuestas genericas...
	//Especifica es estatus code y automaticamente detecta el content type
	public static async Task SendResponse(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, int statusCode, string content)
	{
		await SendResponse(req, res, props, statusCode, content, DetectContentType(content));
	}

	//Esta recibe todo el estatus code,content y content type
	//Para todo tipo de respuestas que no sean 200 o 404
	public static async Task SendResponse(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, int statusCode, string content, string contentType)
	{
		await SendResponse(req, res, props, statusCode, Encoding.UTF8.GetBytes(content), DetectContentType(content));
	}

	//Llama a la funcion de responce generica que en vez de recibir...
	//un string recibe un byte array (capas de mandar cualquier tipo de archivo no solo text)
	public static async Task SendResponse(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, int statusCode, byte[] content, string contentType)
	{
		res.StatusCode = statusCode;
		res.ContentEncoding = Encoding.UTF8;
		res.ContentType = contentType;
		res.ContentLength64 = content.LongLength;
		await res.OutputStream.WriteAsync(content);
		res.Close();
	}

	//Cada vez que se envia un responce se verifica si el resultado es...
	//un error. De serlo se le informa al browser o middleware que sea de chache...
	//que que no guarde la respuesa en el cache
	//Envia la respuesta con status code sugirido por el resultado y...
	//el error (exepcion convertida en string)
	//Si el responce es valido envia la respuesta con status code...
	//sugirido por el resultado y el payload 
	public static async Task SendResultResponse<T>(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Result<T> result)
	{
		if (result.IsError)
		{
			res.Headers["Cache-Control"] = "no-store";

			await HttpUtils.SendResponse(req, res, props, result.StatusCode, result.Error!.ToString()!);
		}
		else
		{
			await HttpUtils.SendResponse(req, res, props, result.StatusCode, result.Payload!.ToString()!);
		}
	}

	//Cuando se quiere mandar una respuesta paginada hay consideraciones...
	//que se hacen (recibe un resultado que adentro tenga el paged result...
	//y tambien maneja/envia a los headers la pagina y el size)
	//Si el resultado es error le dice al middleware que sea de chache...
	//que que no guarde la respuesa en el cache
	//Envia la respuesta con status code sugirido por el resultado y...
	//el error (exepcion convertida en string)
	//Si el responce es valido y posees la lista de resultados de resources...
	//se extraen esos resultados y se le añaden todos los encabezados...
	//pertinentes al proceso de paginacion y luego se manda la respuesta...
	//basada en el satutus code sugerido y el payload(paged results)
	public static async Task SendPagedResultResponse<T>(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Result<PagedResult<T>> result, int page, int size)
	{
		if (result.IsError)
		{
			res.Headers["Cache-Control"] = "no-store";
			await HttpUtils.SendResponse(req, res, props, result.StatusCode, result.Error!.ToString()!);
		}
		else
		{
			var pagedResult = result.Payload!;
			HttpUtils.AddPaginationHeaders(req, res, props, pagedResult, page, size);
			await HttpUtils.SendResponse(req, res, props, result.StatusCode, result.Payload!.ToString()!);
		}
	}

	//Headers que se tiene que añadir cuando se esta enviando informacion paginada
	//Headers custom (X-) no oficiales del protocolo Http,...
	//utilizados para framework, clientes, utilidades,herramientas, extenciones...
	//que permiten explorar listas de items automaticamnete
	//Se calculan la primera, ultima, la anterior y siguiente pagina...
	//Y las aplica como los encabewzados customizados
	public static void AddPaginationHeaders<T>(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, PagedResult<T> pagedResult, int page, int size)
	{
		var baseUrl = $"{req.Url!.Scheme}://{req.Url!.Authority}{req.Url!.AbsolutePath}";
		int totalPages = Math.Max(1, (int)Math.Ceiling((double)pagedResult.TotalCount / size));

		string self = $"{baseUrl}?page={page}&size={size}";
		string? first = page == 1 ? null : $"{baseUrl}?page={1}&size={size}";
		string? last = page == totalPages ? null : $"{baseUrl}?page={totalPages}&size={size}";
		string? prev = page > 1 ? $"{baseUrl}?page={page - 1}&size={size}" : null;
		string? next = page < totalPages ? $"{baseUrl}?page={page + 1}&size={size}" : null;

		res.Headers["Content-Type"] = "application/json; charset=utf-8";
		res.Headers["X-Total-Count"] = pagedResult.TotalCount.ToString();
		res.Headers["X-Page"] = page.ToString();
		res.Headers["X-Page-Size"] = size.ToString();
		res.Headers["X-Total-Pages"] = totalPages.ToString();

		// Optional RFC 5988 Link header for discoverability (Oficial de Http)
		var linkParts = new List<string>();

		if (prev != null) { linkParts.Add($"<{prev}>;  rel=\"prev\""); }
		if (next != null) { linkParts.Add($"<{next}>;  rel=\"next\""); }
		if (first != null) { linkParts.Add($"<{first}>; rel=\"first\""); }
		if (last != null) { linkParts.Add($"<{last}>;  rel=\"last\""); }

		if (linkParts.Count > 0)
		{
			res.Headers["Link"] = string.Join(", ", linkParts);
		}
	}
}
