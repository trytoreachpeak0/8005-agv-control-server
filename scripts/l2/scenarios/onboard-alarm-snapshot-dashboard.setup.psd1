# 「看板可见」要真的起一个看板进程：ControlServer.Dashboard 只经 HTTP 读服务端 /api/dashboard/ 之下的只读
# 端点，所以起不起它不改变服务端的任何行为，而断言读的是它渲染出来的那一页，不是端点。
@{
    Dashboard = $true
}
