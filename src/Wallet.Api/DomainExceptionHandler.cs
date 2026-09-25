using Microsoft.AspNetCore.Diagnostics;
using Wallet.Api.Ledger;

namespace Wallet.Api;

public sealed class DomainExceptionHandler(IProblemDetailsService problemDetails) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(HttpContext ctx, Exception ex, CancellationToken ct)
    {
        var (status, title) = ex switch
        {
            InsufficientFundsException => (StatusCodes.Status422UnprocessableEntity, "Insufficient funds"),
            AccountNotFoundException => (StatusCodes.Status404NotFound, "Account not found"),
            _ => (0, ""),
        };
        if (status == 0) return false;

        ctx.Response.StatusCode = status;
        return await problemDetails.TryWriteAsync(new ProblemDetailsContext
        {
            HttpContext = ctx,
            Exception = ex,
            ProblemDetails = {Status = status, Title = title, Detail = ex.Message},
        });
    }
}