import io, sys
CRIT='src/ControlServer.Host/Runtime/Dispatch/Criteria/DispatchAdmissionCriteria.cs'
FACTS='src/ControlServer.Host/Runtime/Dispatch/Criteria/InTransitVehicleFactsCriterion.cs'

def edit(p, old, new, times=1):
    s=io.open(p,encoding='utf-8',newline='').read()
    assert s.count(old)==times, f'{p}: expected {times} got {s.count(old)}'
    s=s.replace(old,new,times)
    io.open(p,'w',encoding='utf-8',newline='').write(s)

THROW='        ArgumentNullException.ThrowIfNull(idleChain);\n'
which=sys.argv[1]

if which=='A':   # 不派生：忽略传入的空闲链，自己重建一份内容相同的
    edit(CRIT, THROW, THROW+'        idleChain = Default(options, new MapStationResolver(), null!, null!, null!, null!, Microsoft.Extensions.Logging.Abstractions.NullLogger<SlotCapacityCriterion>.Instance);\n')
elif which=='B': # 换上去那条判据挪了位置
    edit(FACTS, 'public int Order => 80;', 'public int Order => 81;')
elif which=='C': # 没有路网也把追加门装上
    edit(CRIT, '''        if (routeGraph is not null)
        {
            criteria.Add(new EnRouteAppendCriterion(routeGraph));
        }

        return criteria;
    }

    /// <summary>Registers''', '''        criteria.Add(new EnRouteAppendCriterion(routeGraph!));

        return criteria;
    }

    /// <summary>Registers''')
elif which=='D': # 某条共用判据装了两次
    edit(CRIT, '''             new InTransitVehicleFactsCriterion(options)];
''', '''             new InTransitVehicleFactsCriterion(options)];
        criteria.Add(criteria[3]);
''')
elif which=='E': # 过滤器多吃掉一条共用判据
    edit(CRIT, 'criterion is not VehicleDynamicFactsCriterion)', 'criterion is not VehicleDynamicFactsCriterion && criterion is not AreaScopeCriterion)')
else:
    raise SystemExit('unknown ' + which)
print('injected', which)
