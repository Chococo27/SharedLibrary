namespace Shared.Http;

//Consiste del total de values que estan contenidos en el repositorio o ...
//el recurso que se esta dirigiendo y el subset que se pagino.
public class PagedResult<T>
{
	public int TotalCount { get; }
	public List<T> Values { get; }

	public PagedResult(int totalCount, List<T> values)
	{
		TotalCount = totalCount;
		Values = values;
	}
}