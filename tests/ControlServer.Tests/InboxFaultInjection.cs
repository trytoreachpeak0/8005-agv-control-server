using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ControlServer.Tests;

/// <summary>
/// Throws on the ProtocolInbox statements that carry one messageId (control-server#779): the dedup lookup (before processing),
/// the insert (after processing, inside its transaction), or whatever statement comes next after the lookup -- the first one
/// a replay callback runs. Counts every lookup and insert it sees for that messageId.
/// </summary>
internal sealed class InboxFault
{
    private string? _messageId;
    private int _lookupFailures;
    private int _lookupsToPass;
    private int _insertFailures;
    private int _afterLookupFailures;
    private bool _lookupSeen;
    private Func<Exception> _lookupFault = () => new InvalidOperationException();
    private Func<Exception> _insertFault = () => new InvalidOperationException();
    private Func<Exception> _afterLookupFault = () => new InvalidOperationException();

    public int Lookups { get; private set; }

    public int Inserts { get; private set; }

    /// <param name="pass">Lookups to let through first: a field confirmation reads the inbox once before its dedup lookup.</param>
    public void FailLookup(string messageId, int times, Func<Exception> fault, int pass = 0)
    {
        _messageId = messageId;
        _lookupsToPass = pass;
        _lookupFailures = times;
        _lookupFault = fault;
    }

    public void FailInsert(string messageId, int times, Func<Exception> fault)
    {
        _messageId = messageId;
        _insertFailures = times;
        _insertFault = fault;
    }

    /// <summary>Fails the first statement of any kind run after the lookup for this messageId, once.</summary>
    public void FailAfterLookup(string messageId, Func<Exception> fault)
    {
        _messageId = messageId;
        _afterLookupFailures = 1;
        _lookupSeen = false;
        _afterLookupFault = fault;
    }

    public void Inspect(DbCommand command)
    {
        if (_messageId is null)
        {
            return;
        }
        string sql = command.CommandText;
        bool carriesMessageId = command.Parameters.Cast<DbParameter>().Any(parameter => Equals(parameter.Value, _messageId));
        bool lookup = carriesMessageId &&
                      sql.StartsWith("SELECT", StringComparison.Ordinal) &&
                      sql.Contains("FROM \"ProtocolInbox\"", StringComparison.Ordinal);
        if (!lookup && _lookupSeen && _afterLookupFailures > 0)
        {
            _afterLookupFailures--;
            throw _afterLookupFault();
        }
        if (!carriesMessageId)
        {
            return;
        }
        if (sql.Contains("INSERT INTO \"ProtocolInbox\"", StringComparison.Ordinal))
        {
            Inserts++;
            if (_insertFailures > 0)
            {
                _insertFailures--;
                throw _insertFault();
            }
        }
        else if (lookup)
        {
            Lookups++;
            if (_lookupsToPass > 0)
            {
                _lookupsToPass--;
            }
            else if (_lookupFailures > 0)
            {
                _lookupFailures--;
                throw _lookupFault();
            }
            _lookupSeen = true;
        }
    }
}

/// <summary>Hands every command the context runs to an <see cref="InboxFault"/>.</summary>
internal sealed class InboxFaultInterceptor(InboxFault fault) : DbCommandInterceptor
{
    public override InterceptionResult<DbDataReader> ReaderExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result)
    {
        fault.Inspect(command);
        return result;
    }

    public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<DbDataReader> result,
        CancellationToken cancellationToken = default)
    {
        fault.Inspect(command);
        return ValueTask.FromResult(result);
    }

    public override InterceptionResult<int> NonQueryExecuting(
        DbCommand command, CommandEventData eventData, InterceptionResult<int> result)
    {
        fault.Inspect(command);
        return result;
    }

    public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
        DbCommand command,
        CommandEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        fault.Inspect(command);
        return ValueTask.FromResult(result);
    }
}
