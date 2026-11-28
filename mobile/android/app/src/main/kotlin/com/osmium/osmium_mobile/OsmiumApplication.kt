package com.osmium.osmium_mobile

import android.app.Application
import android.os.Process
import java.util.Date
import kotlin.system.exitProcess

class OsmiumApplication : Application() {
    override fun onCreate() {
        super.onCreate()
        val previousHandler = Thread.getDefaultUncaughtExceptionHandler()
        Thread.setDefaultUncaughtExceptionHandler { thread, error ->
            try {
                filesDir.resolve("jvm-crash.log").writeText(
                    "[${Date()}] Thread=${thread.name}\n${error.stackTraceToString()}"
                )
            } catch (_: Exception) {
                // Keep the original exception when local logging fails.
            } finally {
                if (previousHandler != null) {
                    previousHandler.uncaughtException(thread, error)
                } else {
                    Process.killProcess(Process.myPid())
                    exitProcess(10)
                }
            }
        }
    }
}
