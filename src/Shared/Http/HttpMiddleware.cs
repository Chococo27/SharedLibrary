namespace Shared.Http;

using System.Collections;
using System.Net;

//Middleware es un delegado
//Permite que cualquier funcion o metodo que que tenga estas firmas/parametros...
//en su lista de parametros se pueda utilizar o convertir en un moddleware
public delegate Task HttpMiddleware(HttpListenerRequest req, HttpListenerResponse res, Hashtable props, Func<Task> next);