package com.macroclicker.mobile

import android.app.Application
import com.google.android.material.color.DynamicColors
import com.macroclicker.mobile.shell.ShellExecutor

/** Android 12+ 跟随壁纸动态取色，低版本回退品牌色板。 */
class App : Application() {
    override fun onCreate() {
        super.onCreate()
        DynamicColors.applyToActivitiesIfAvailable(this)
        ShellExecutor.init(this)
    }
}
