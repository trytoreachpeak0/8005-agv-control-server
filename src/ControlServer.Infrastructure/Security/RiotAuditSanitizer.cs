using ControlServer.Application;

namespace ControlServer.Infrastructure.Security;

internal static class RiotAuditSanitizer
{
    public static RiotOrderCallReceipt? Receipt(RiotOrderCallReceipt? receipt) =>
        receipt is null
            ? null
            : receipt with { BusinessCode = BusinessCode(receipt.BusinessCode) };

    public static string? BusinessCode(string? value)
    {
        if (!string.IsNullOrEmpty(value) &&
            value.Length <= 16 &&
            value.All(char.IsAsciiDigit))
        {
            return value;
        }

        return value switch
        {
            "order-ref-missing" or
            "order-state-page-missing" or
            "order-upper-id-mismatch" or
            "riot-read-failed" or
            "riot-read-timeout" or
            "riot-response-invalid" => value,
            _ => null
        };
    }
}
