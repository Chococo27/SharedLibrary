namespace Shared.Http;

using System.Net;

//Clase utilitaria que nos permite conceptualizar y encapsular lo que seria...
//el resultado de una operacion a nivel de servicio o repositorio 
//Si la operacion resulta en error en vez de permitir que el...
//centralized error handling lo coja lo coje el Result (InternalServerError)
//Los errores dentro de una capa en particular (repositorio y Servicio)...
//que sean esperados (Ej. Validacion) son capturados y devuelven...
//respuestas User friendly
//Los errores se cojen y se pasa adelante de manera controlada
//Si todo salio bien el resultado va a ser el payload y un status code...
//que refleje la condicion mas comun de la operacion (OK)
public class Result<T>
{
	public bool IsError { get; }
	public Exception? Error { get; }
	public T? Payload { get; }
	public int StatusCode { get; }

	public Result(Exception error, int statusCode = (int)HttpStatusCode.InternalServerError)
	{
		IsError = true;
		Error = error;
		Payload = default(T);
		StatusCode = statusCode;
	}

	public Result(T payload, int statusCode = (int)HttpStatusCode.OK)
	{
		IsError = false;
		Error = null;
		Payload = payload;
		StatusCode = statusCode;
	}
}