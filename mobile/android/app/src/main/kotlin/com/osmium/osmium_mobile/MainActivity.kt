package com.osmium.osmium_mobile

import android.content.Intent
import android.content.pm.ActivityInfo
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Matrix
import android.media.ExifInterface
import android.os.Build
import android.os.Bundle
import android.util.Base64
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
import java.io.ByteArrayInputStream
import java.io.ByteArrayOutputStream
import java.util.concurrent.Executors
import kotlin.math.min

class MainActivity : FlutterActivity() {
    private var surfaceBridge: VideoSurfaceBridge? = null
    private val inputBridge = InputCaptureBridge()
    private val avatarExecutor = Executors.newSingleThreadExecutor()
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
                "compressAvatar" -> {
                    val rawBytes = call.argument<ByteArray>("bytes")
                    if (rawBytes == null || rawBytes.isEmpty()) {
                        result.success(null)
                    } else {
                        avatarExecutor.execute {
                            val dataUri = compressAvatarToDataUri(rawBytes)
                            runOnUiThread { result.success(dataUri) }
                        }
                    }
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

    private fun compressAvatarToDataUri(rawBytes: ByteArray): String? {
        return try {
            val orientation = try {
                ExifInterface(ByteArrayInputStream(rawBytes)).getAttributeInt(
                    ExifInterface.TAG_ORIENTATION,
                    ExifInterface.ORIENTATION_NORMAL
                )
            } catch (_: Exception) {
                ExifInterface.ORIENTATION_NORMAL
            }

            val bounds = BitmapFactory.Options().apply { inJustDecodeBounds = true }
            BitmapFactory.decodeByteArray(rawBytes, 0, rawBytes.size, bounds)
            if (bounds.outWidth <= 0 || bounds.outHeight <= 0) return null

            var sampleSize = 1
            val minSide = min(bounds.outWidth, bounds.outHeight)
            while (minSide / (sampleSize * 2) >= 256) {
                sampleSize *= 2
            }

            val decodeOpts = BitmapFactory.Options().apply { inSampleSize = sampleSize }
            var decoded = BitmapFactory.decodeByteArray(rawBytes, 0, rawBytes.size, decodeOpts)
                ?: return null

            val matrix = Matrix()
            when (orientation) {
                ExifInterface.ORIENTATION_ROTATE_90 -> matrix.postRotate(90f)
                ExifInterface.ORIENTATION_ROTATE_180 -> matrix.postRotate(180f)
                ExifInterface.ORIENTATION_ROTATE_270 -> matrix.postRotate(270f)
                ExifInterface.ORIENTATION_FLIP_HORIZONTAL -> matrix.preScale(-1f, 1f)
                ExifInterface.ORIENTATION_FLIP_VERTICAL -> matrix.preScale(1f, -1f)
                ExifInterface.ORIENTATION_TRANSPOSE -> {
                    matrix.postRotate(90f)
                    matrix.preScale(-1f, 1f)
                }
                ExifInterface.ORIENTATION_TRANSVERSE -> {
                    matrix.postRotate(270f)
                    matrix.preScale(-1f, 1f)
                }
            }
            if (!matrix.isIdentity) {
                val oriented = Bitmap.createBitmap(
                    decoded,
                    0,
                    0,
                    decoded.width,
                    decoded.height,
                    matrix,
                    true
                )
                if (oriented != decoded) {
                    decoded.recycle()
                    decoded = oriented
                }
            }

            val side = min(decoded.width, decoded.height)
            val cropX = (decoded.width - side) / 2
            val cropY = (decoded.height - side) / 2
            val cropped = Bitmap.createBitmap(decoded, cropX, cropY, side, side)
            if (cropped != decoded) decoded.recycle()

            var scaled = Bitmap.createScaledBitmap(cropped, 128, 128, true)
            if (scaled != cropped) cropped.recycle()

            val targetMaxBytes = 48 * 1024
            val hardMaxBytes = 64 * 1024
            val out = ByteArrayOutputStream()
            var quality = 90
            while (quality >= 30) {
                out.reset()
                scaled.compress(Bitmap.CompressFormat.JPEG, quality, out)
                if (out.size() <= targetMaxBytes) break
                quality -= 8
            }

            if (out.size() > hardMaxBytes) {
                val smaller = Bitmap.createScaledBitmap(scaled, 96, 96, true)
                if (smaller != scaled) {
                    scaled.recycle()
                    scaled = smaller
                }
                out.reset()
                scaled.compress(Bitmap.CompressFormat.JPEG, 70, out)
            }

            scaled.recycle()
            val bytes = out.toByteArray()
            if (bytes.isEmpty() || bytes.size > hardMaxBytes) return null
            "data:image/jpeg;base64," + Base64.encodeToString(bytes, Base64.NO_WRAP)
        } catch (_: Exception) {
            null
        }
    }

    override fun onDestroy() {
        avatarExecutor.shutdownNow()
        surfaceBridge?.destroySurface()
        super.onDestroy()
    }
}
