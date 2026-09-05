// Shizuku UserService：本接口实现类运行在 Shizuku 服务进程（shell uid），
// 供本应用经 Binder 调用注入触摸事件（tap / swipe=滑动或长按）。
//
// 双通道设计：
//  - 快速路径：反射调用系统 InputManager.injectInputEvent（UserService 进程
//    不受隐藏 API 限制），单次注入毫秒级，不创建任何子进程；
//  - 兼容路径：ProcessBuilder 执行固定 argv 的 /system/bin/input tap|swipe。
//  所有坐标均为 int 数值，无字符串拼接，不存在 shell 注入面。
package com.macroclicker.mobile.inject;

interface IInjectorService {

    // 能力探测：1 = 快速路径可用；0 = 仅兼容模式
    int probe() = 1;

    // 注入点击；返回 0 成功，负数失败
    int tap(int x, int y) = 2;

    // 注入滑动/长按（起止同点即长按）；durationMs 为毫秒
    int swipe(int x1, int y1, int x2, int y2, int durationMs) = 3;

    // Shizuku 约定的销毁方法（进程退出）
    void destroy() = 16777114;
}
