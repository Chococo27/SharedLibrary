namespace Shared.Http;

using System.Collections;
using System.Collections.Specialized;
using System.Net;
using System.Web;

public class HttpRouter
{
	public const int RESPONSE_NOT_SENT = 777;

	private static ulong requestId = 0;
	private string basePath;
	private List<HttpMiddleware> middlewares;
	private List<(string, string, HttpMiddleware[])> routes;

	public HttpRouter()
	{
		basePath = string.Empty;
		middlewares = [];
		routes = [];
	}

	//Global Middleware
	//Permite instalar middlewares globales
	//Regresa la misma instancia (this) del router lo que permite crear...
	//patrones de chaining
	public HttpRouter Use(params HttpMiddleware[] middlewares)
	{
		this.middlewares.AddRange(middlewares);
		return this;
	}

	//Per-Route Middleware 
	//Permite mappear el metodo, la ruta y la lista de middlewares
	//Similar al middleware global tambien regresa la misma instancia (this)
	//Añade metodos convenientes de Get, Post, Put y Delete
	public HttpRouter Map(string method, string path, params HttpMiddleware[] middlewares)
	{
		routes.Add((method.ToUpperInvariant(), path, middlewares));

		return this;
	}

	public HttpRouter MapGet(string path, params HttpMiddleware[] middlewares)
	{
		return Map("GET", path, middlewares);
	}

	public HttpRouter MapPost(string path, params HttpMiddleware[] middlewares)
	{
		return Map("POST", path, middlewares);
	}

	public HttpRouter MapPut(string path, params HttpMiddleware[] middlewares)
	{
		return Map("PUT", path, middlewares);
	}

	public HttpRouter MapDelete(string path, params HttpMiddleware[] middlewares)
	{
		return Map("DELETE", path, middlewares);
	}

	//Front Controller 
	//Pasa el contexto del request Http Server y lo desempaca
	//Extrae el request y responce, crea un nuevo hashtable con las...
	//propiedades y lo pasa al HandleAsync metodo que brega con el contexto de Http
	//req = contiene el URL, metodo y query string. Puede que tenga...
	//informacion en el body necesaria para satisfacer el request
	//res = objeto que se utiliza para escribir la respueata al request...
	//Posee sus headers, payloads, contenido, etc. Sobre todo tiene status code
	//props= es un dato estructurado que se utilizara para comunicar...
	//los diferentes middlewares
	//En "finally" si ningun middleware dentro del pipeline, sea global o de ruta,...
	//escribe una respuesta, automaticamente escribe una respuesta de ...
	// "not implemented" y cierra la respuesta. Tambien fuera a algun...
	//middleware recibir el request y mandar una respuesta pero olvido cerrar..
	// el "finally" se encarga de cerrarla

	public async Task HandleContextAsync(HttpListenerContext ctx)
	{
		var req = ctx.Request;
		var res = ctx.Response;
		var props = new Hashtable();
		res.StatusCode = RESPONSE_NOT_SENT;
		props["req.id"] = ++requestId;
		try
		{
			await HandleAsync(req, res, props, () => Task.CompletedTask);
		}
		finally
		{
			if (res.StatusCode == RESPONSE_NOT_SENT)
			{
				res.StatusCode = (int)HttpStatusCode.NotImplemented;
			}
			res.Close();
		}
	}

	//Router as Middleware
	//Posee el mismo sinature que "EL PROFE" dijo podia ser un middleware
	//lo cual significa que puede servir de middleware
	//Aserlo de esta manera nos permite usar o añadir un router como middleware
	//Crea un Pipeline basandose en los global middlewares y lo pone a correr
	//Dado a que de por si es un middleware llama al siguiente (next)
	private async Task HandleAsync(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
	{
		Func<Task> globalMiddlewarePipeline = GenerateMiddlewarePipeline(req, res, props, middlewares);
		await globalMiddlewarePipeline();
		await next();
	}

	//Hierarchical Routing
	//Indica el path que va a atender el router
	//Setea el router base path al pariente (el router actual)...
	//mas la ruta que se mando de parametros
	//Luego añade este router como middleware global a la lista de global middleware
	//Esta funcion y el HandleAsync permite crear subrouters o jerarquias de routers
	public HttpRouter UseRouter(string path, HttpRouter router)
	{
		router.basePath = this.basePath + path;
		return Use(router.HandleAsync);
	}

	//Middleware Pipeline Generator 
	//Es un high order function (funcion que devuelve o recibe funciones...
	// o cullos parametros podrian ser o devuelvan funciones)
	//Define la funcion de next cual inicialmente no hace nada y regresa
	//Luego espera, redefine y dice que next va a ser una funcion asyncronnica...
	//basada en el indice cual empieza en -1 y aumenta. Si el indice no se pasa...
	//de la cuenta de los middlewares va al proximo middleware a ejecutar que indica el index
	//Se pasa a si misma a la funcion del middleware que se esta llamando
	private Func<Task> GenerateMiddlewarePipeline(HttpListenerRequest req,
	HttpListenerResponse res, Hashtable props, List<HttpMiddleware> middlewares)
	{
		int index = -1;

		Func<Task> next = () => Task.CompletedTask;

		next = async () =>
		{
			index++;
			if (index < middlewares.Count && res.StatusCode == RESPONSE_NOT_SENT)
			{
				await middlewares[index](req, res, props, next);
			}
		};
		return next;
	}

	//Route Matching
	//Instala middlewares llamado SimpleRouteMatching o ParametrizedRouteMatching
	public HttpRouter UseSimpleRouteMatching()
	{
		return Use(SimpleRouteMatching);
	}

	public HttpRouter UseParametrizedRouteMatching()
	{
		return Use(ParametrizedRouteMatching);
	}

	//Simple Route Matching 
	//Es un middleware cullo proposito es machear la ruta exactamente...
	//por metodo y por path
	//Verifica que el metodo del request es igual al metodo de la ruta...
	//y que el path de URL del request es igual al path y el basepath
	//De coinsidir genera el pipeline de per route middleware(middleware que le corresponde a la ruta)
	//Luego termina, sale del for loop e invoca el siguiente middleware 
	private async Task SimpleRouteMatching(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
	{
		foreach (var (method, path, middlewares) in routes)
		{
			if (req.HttpMethod == method && string.Equals(req.Url!.AbsolutePath, basePath + path)) // * 
			{
				Func<Task> routeMiddlewarePipeline = GenerateMiddlewarePipeline(req, res, props, middlewares.ToList());

				await routeMiddlewarePipeline();

				break; // or return; // To short-circuit global pipeline. 
			}
		}

		await next();
	}

	//Parametrized Route Matching
	//Contrario al SimpleRouteMatching cual le la ruta de manera exacta/literal
	//ParametrizedRouteMatching puede reconocer parametros y los sustitulle con lo...
	//que encuentre en el request path
	//Las partes de la ruta que sean estaticas las machea y las parametrisadas las..
	//machea y aparea.
	//Comienza macheando los metodos luego trata de parsear o machear los parametros...
	//mientras intenta machear los parametros crea el mapping cual mapea el nombre...
	//del parametro. Una vez machea los parametros los mete dentro de props para...
	//que sean accesibles a futuros middlewares y controladores y satisfacer el request.
	//genera el pipeline de per route middleware(middleware que le corresponde a la ruta)
	//Luego termina, sale del for loop e invoca el siguiente middleware 
	private async Task ParametrizedRouteMatching(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next)
	{
		foreach (var (method, path, middlewares) in routes)
		{
			NameValueCollection? parameters;

			if (req.HttpMethod == method && (parameters = ParseUrlParams(req.Url!.AbsolutePath, basePath + path)) != null) // * 
			{
				props["req.params"] = parameters;

				Func<Task> routeMiddlewarePipeline = GenerateMiddlewarePipeline(req, res, props, middlewares.ToList());

				await routeMiddlewarePipeline();

				break; // or return; // To short-circuit global pipeline. 
			}
		}

		await next();
	}

	//Parsing Parametrized Routes 
	//Coje el path de la ruta y el reques y les remueven los / tanto al principio como el final
	// y le remueve los espacios y empy strings
	//de no poseer el mismo numero de / significa que las rutas no son iguales
	//de poseer el mismo numero de / comienza a investigar
	//crea la lista de parametros vacia y compara por cada parte
	//si algun segmento de la ruta posee : significa que es parametro
	//toma el segmento correspondiente y lo parea con el request
	//fuera el segmento no empiezar : y no ser iguale al request devuelve null
	public static NameValueCollection? ParseUrlParams(string uPath, string rPath)
	{
		string[] uParts = uPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
		string[] rParts = rPath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

		if (uParts.Length != rParts.Length) { return null; }

		var parameters = new NameValueCollection();

		for (int i = 0; i < rParts.Length; i++)
		{
			string uPart = uParts[i];
			string rPart = rParts[i];

			if (rPart.StartsWith(":"))
			{
				string paramName = rPart.Substring(1);
				parameters[paramName] = HttpUtility.UrlDecode(uPart);
			}
			else if (uPart != rPart)
			{
				return null;
			}
		}
		return parameters;
	}

}