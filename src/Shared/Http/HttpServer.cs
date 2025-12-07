namespace Shared.Http;

using Shared.Config;
using System.Net;

//Inicializa el router
//Llama metodo Init cual esta vacio es un metodo abstracto
//En el Init es dnde ocurre todo el wiring de rutas, dependency injection...
//inicializacion de database, repositorios servicios, controladores, mappping...
//instalacion del global middleware y per route middlewares
//Toda la aplicacion va a heredar o hacer una subclase de Http serve rquien va a...
//re-escribir el metodo de Init con todo lo previamente mencionado que sea...
//necesario para implementar su proposito

//Luego de ensamblar todo busca la configuracion un archivo que sea cfg...
//y busca la variable HOST de no haber busca variable default (http://127.0.0.1)
//Similarmente busca la variable POST de no haber busca variable default (5000)
//Una vez completado inicializa el Http listener

public abstract class HttpServer
{
	protected HttpRouter router;
	protected HttpListener server;

	public HttpServer()
	{
		router = new HttpRouter();

		Init();

		string host = Configuration.Get<string>("HOST", "http://127.0.0.1");
		string port = Configuration.Get<string>("PORT", "5000");
		string authority = $"{host}:{port}/";

		server = new HttpListener();
		server.Prefixes.Add(authority);

		Console.WriteLine("Server started at " + authority);
	}

	public abstract void Init();

	//Inicializa y comienza a escuchar esperando un request cual...
	//al recibirlo guardara el contexto y se lo pasa al HandleContextAsync
	public async Task Start()
	{
		server.Start();

		while (server.IsListening)
		{
			HttpListenerContext ctx = await server.GetContextAsync();

			_ = router.HandleContextAsync(ctx);
		}
	}

	//Apaga el server
	public void Stop()
	{
		if (server.IsListening)
		{
			server.Stop();
			server.Close();
			Console.WriteLine("Server stopped.");
		}
	}
}