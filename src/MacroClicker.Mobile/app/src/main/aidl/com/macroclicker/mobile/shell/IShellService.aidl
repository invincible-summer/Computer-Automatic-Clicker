// Shizuku UserService：本接口实现类运行在 Shizuku 服务进程（shell uid），
// 供本应用经 Binder 调用执行固定参数的 input 命令（tap / swipe）。
package com.macroclicker.mobile.shell;

interface IShellService {

    // Shizuku 约定的销毁方法（进程退出）
    void destroy() = 16777114;

    // 执行固定 argv 命令并等待退出；返回进程退出码，超时/异常返回负值
    // 注意：SDK aidl 要求事务 ID 全有或全无，故 exec 也显式分配
    int exec(in String[] cmd) = 1;
}
