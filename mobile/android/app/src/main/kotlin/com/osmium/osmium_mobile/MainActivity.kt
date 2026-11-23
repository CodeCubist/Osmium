package com.osmium.osmium_mobile

import android.content.Intent
import android.content.pm.ActivityInfo
import android.os.Build
import android.os.Bundle
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.WindowManager
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import io.flutter.embedding.android.FlutterActivity
import io.flutter.embedding.engine.FlutterEngine
import io.flutter.plugin.common.EventChannel
import io.flutter.plugin.common.MethodChannel

class MainActivity : FlutterActivity() {
    private var surfaceBridge: VideoSurfaceBridge? = null
    private val inputBridge = InputCaptureBridge()
    private var pointerCaptureActive = false
    private var initialDeepLink: String? = null
    private var deepLinkSink: EventChannel.EventSink? = null

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        extractDeepLink(intent)?.let { initialDeepLink = it }
        WindowCompat.setDecorFitsSystemWindows(window, false)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            window.attributes.layoutInDisplayCutoutMode =
                WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
        }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        val link = extractDeepLink(intent) ?: return
        val sink = deepLinkSink
        if (sink != null) {
            runOnUiThread { sink.success(link) }
        } else {
            initialDeepLink = link
        }
    }

    private fun extractDeepLink(intent: Intent?): String? {
        if (intent == null || intent.action != Intent.ACTION_VIEW) return null
        val uri = intent.data ?: return null
        if (!uri.scheme.equals("osmium", ignoreCase = true)) return null
        return uri.toString()
    }

    override fun configureFlutterEngine(flutterEngine: FlutterEngine) {
        super.configureFlutterEngine(flutterEngine)

        surfaceBridge = VideoSurfaceBridge(flutterEngine.renderer) { handleId ->
            runOnUiThread {
                inputBridge.emitSurfaceChanged(handleId)
            }
        }

        EventChannel(
            flutterEngine.dartExecutor.binaryMessenger,
            "com.osmium.mobile/input_events"
        ).setStreamHandler(inputBridge)

        EventChannel(
            flutterEngine.dartExecutor.binaryMessenger,
            "com.osmium.mobile/deep_links"
        ).setStreamHandler(object : EventChannel.StreamHandler {
            override fun onListen(arguments: Any?, events: EventChannel.EventSink?) {
                deepLinkSink = events
            }

            override fun onCancel(arguments: Any?) {
                deepLinkSink = null
            }
        })

        MethodChannel(
            flutterEngine.dartExecutor.binaryMessenger,
            "com.osmium.mobile/native"
        ).setMethodCallHandler { call, result ->
            when (call.method) {
                "getInitialDeepLink" -> {
                    val link = initialDeepLink
                    initialDeepLink = null
                    result.success(link)
                }
                "createSurface" -> {
                    val width = call.argument<Int>("width") ?: 1920
                    val height = call.argument<Int>("height") ?: 1080
                    val info = surfaceBridge?.createSurface(width, height)
                    result.success(info)
                }
                "resizeSurface" -> {
                    val width = call.argument<Int>("width") ?: 1920
                    val height = call.argument<Int>("height") ?: 1080
                    surfaceBridge?.resizeSurface(width, height)
                    result.success(true)
                }
                "destroySurface" -> {
                    surfaceBridge?.destroySurface()
                    result.success(true)
                }
                "setPointerCapture" -> {
                    val enabled = call.argument<Boolean>("enabled") ?: false
                    setPointerCaptureEnabled(enabled)
                    result.success(pointerCaptureActive)
                }
                "setImmersiveMode" -> {
                    val enabled = call.argument<Boolean>("enabled") ?: false
                    setImmersiveSystemBars(enabled)
                    result.success(true)
                }
                "setOrientationLock" -> {
                    // 0 = auto/sensor, 1 = portrait, 2 = landscape
                    val mode = call.argument<Int>("mode") ?: 0
                    requestedOrientation = when (mode) {
                        1 -> ActivityInfo.SCREEN_ORIENTATION_SENSOR_PORTRAIT
                        2 -> ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE
                        else -> ActivityInfo.SCREEN_ORIENTATION_FULL_SENSOR
                    }
                    result.success(true)
                }
                else -> result.notImplemented()
            }
        }
    }

    private fun setPointerCaptureEnabled(enabled: Boolean) {
        val decor = window.decorView
        if (enabled) {
            decor.isFocusable = true
            decor.isFocusableInTouchMode = true
            decor.requestFocus()
            decor.setOnCapturedPointerListener { _, event ->
                inputBridge.handleCapturedPointerEvent(event)
            }
            decor.requestPointerCapture()
            pointerCaptureActive = true
        } else {
            decor.releasePointerCapture()
            decor.setOnCapturedPointerListener(null)
            pointerCaptureActive = false
        }
    }

    private fun setImmersiveSystemBars(enabled: Boolean) {
        val controller = WindowCompat.getInsetsController(window, window.decorView)
        if (enabled) {
            controller.systemBarsBehavior =
                WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
            controller.hide(WindowInsetsCompat.Type.systemBars())
        } else {
            controller.show(WindowInsetsCompat.Type.systemBars())
        }
    }

    override fun dispatchKeyEvent(event: KeyEvent): Boolean {
        if (inputBridge.handleGamepadKeyEvent(event)) {
            return true
        }
        return super.dispatchKeyEvent(event)
    }

    override fun dispatchGenericMotionEvent(event: MotionEvent): Boolean {
        if (inputBridge.handleGamepadMotionEvent(event)) {
            return true
        }
        return super.dispatchGenericMotionEvent(event)
    }

    override fun onDestroy() {
        surfaceBridge?.destroySurface()
        super.onDestroy()
    }
}
