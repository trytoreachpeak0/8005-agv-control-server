import io, re, sys
P='src/ControlServer.Host/Runtime/Dispatch/Criteria/InTransitVehicleFactsCriterion.cs'
s=io.open(P,encoding='utf-8',newline='').read()
HEAD='        ArgumentNullException.ThrowIfNull(facts);\n        ArgumentNullException.ThrowIfNull(options);\n'
TAIL='        return DispatchAdmissionChain.Eligible;\n    }\n}\n'
which=sys.argv[1]
if which in ('F','G'):
    i=s.index(HEAD)+len(HEAD); j=s.index(TAIL)
    body = ('        return DispatchAdmissionChain.Eligible;\n'
            if which=='F' else
            '        return VehicleDynamicFactsCriterion.Evaluate(facts, options);\n')
    s = s[:i] + '\n' + body + '    }\n}\n'
elif which=='H':
    old='if (!facts.Onboard.DepartureSafe || !facts.Onboard.AllUnlockOutputsReset || facts.Onboard.UnknownPresent)'
    new='if (!facts.Onboard.DepartureSafe || !facts.Onboard.AllUnlockOutputsReset)'
    assert s.count(old)==1
    s=s.replace(old,new,1)
else:
    raise SystemExit('unknown')
io.open(P,'w',encoding='utf-8',newline='').write(s)
print('injected', which)
