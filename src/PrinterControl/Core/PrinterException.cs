using System.Net;

namespace PrinterControl.Core;

internal enum PrinterFailure
{
	Unreachable,
	Unauthorized,
	Forbidden,
	NotFound,
	Conflict,
	BadRequest,
	ServerError,
	InvalidResponse,
	NotSupported,
}

internal sealed class PrinterException : Exception
{
	public PrinterException(PrinterFailure failure, string message, Exception? inner = null)
		: base(message, inner) => Failure = failure;

	public PrinterFailure Failure { get; }

	public static PrinterFailure FromStatus(HttpStatusCode status) => status switch
	{
		HttpStatusCode.Unauthorized => PrinterFailure.Unauthorized,
		HttpStatusCode.Forbidden => PrinterFailure.Forbidden,
		HttpStatusCode.NotFound => PrinterFailure.NotFound,
		HttpStatusCode.Conflict => PrinterFailure.Conflict,
		HttpStatusCode.BadRequest => PrinterFailure.BadRequest,
		_ => PrinterFailure.ServerError,
	};
}
