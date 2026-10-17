import sys

ENGINE = 'src/ControlServer.Host/Runtime/JourneyRuntimeEngine.cs'
STAGING = 'src/ControlServer.Host/Runtime/JourneyRuntimeEngine.StagingDeparture.cs'

MUTATIONS = {
    # review 1: Loaded counted as ended
    'M1': (ENGINE,
           'atStop.All(item => item.Membership.Status == JourneyDemandStatuses.Terminated)',
           'atStop.All(item => item.Membership.Status is JourneyDemandStatuses.Terminated or JourneyDemandStatuses.Loaded)'),
    # review 2: new branch ahead of the same-station grace
    'M2': (ENGINE,
           '        if (stops.OpenStops.FirstOrDefault(stop => stop.Sequence > stops.Current.Sequence) is\n',
           '        if (!EveryDemandAtCurrentStopEnded(stops) && stops.OpenStops.FirstOrDefault(stop => stop.Sequence > stops.Current.Sequence) is\n'),
    # review 4: only the requested version checked
    'M4': (STAGING,
           'for (int offset = 0; offset < ids.Length; offset++)',
           'for (int offset = 0; offset < 1; offset++)'),
    # review 5: correction check never refuses
    'M5': (STAGING,
           '.Any(createdAt => createdAt > since);',
           '.Any(createdAt => false);'),
    # review 6: both staging exclusions of the new branch removed
    'M6': (ENGINE,
           'if (deadline is not null && !IsStagingPickup(stops.Current) && EveryDemandAtCurrentStopEnded(stops))',
           'if (EveryDemandAtCurrentStopEnded(stops))'),
    # own: condition 1 range check removed
    'M7': (STAGING,
           'if (stop.StopRole != JourneyStopRoles.Pickup || IsStagingPickup(stop) ||\n            requested < locked.FirstWorklistRevisionAt(runtime.WorklistRevision, stop))',
           'if (stop.StopRole != JourneyStopRoles.Pickup || IsStagingPickup(stop))'),
    # own: "more loaded" side never refuses
    'M8': (STAGING,
           "reported.Any(id => !receivedAt.TryGetValue(id, out DateTimeOffset at) || at > since))",
           "reported.Any(id => !receivedAt.TryGetValue(id, out DateTimeOffset at)))"),
    # own: "fewer loaded" side removed
    'M9': (STAGING,
           '                if (committedLoads.Contains(member.LoadSlotOperationAttemptId))\n                {\n                    return false;\n                }\n',
           ''),
    # own: staging exclusion of the stale rule removed
    'M10': (STAGING,
            'if (stop.StopRole != JourneyStopRoles.Pickup || IsStagingPickup(stop) ||',
            'if (stop.StopRole != JourneyStopRoles.Pickup ||'),
    # own: condition 3 items check removed
    'M11': (STAGING,
            "payload.GetProperty(\"items\").GetArrayLength() != 0)",
            "false)"),
}

name = sys.argv[1]
path, old, new = MUTATIONS[name]
with open(path, encoding='utf-8', newline='') as f:
    text = f.read()
old_n = old.replace('\n', '\r\n') if '\r\n' in text else old
new_n = new.replace('\n', '\r\n') if '\r\n' in text else new
count = text.count(old_n)
if count != 1:
    print(f'{name}: pattern found {count} times in {path}', file=sys.stderr)
    sys.exit(4)
with open(path, 'w', encoding='utf-8', newline='') as f:
    f.write(text.replace(old_n, new_n))
print(path)
