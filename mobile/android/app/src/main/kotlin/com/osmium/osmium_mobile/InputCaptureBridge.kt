package com.osmium.osmium_mobile

import android.view.InputDevice
import android.view.KeyEvent
import android.view.MotionEvent
import io.flutter.plugin.common.EventChannel
import kotlin.math.abs
import kotlin.math.roundToInt

class InputCaptureBridge : EventChannel.StreamHandler {
    private var eventSink: EventChannel.EventSink? = null
    private var lastMouseButtons: Int = 0
    private var gamepadButtonsMask: Int = 0
    private var lastLx: Int = 0
    private var lastLy: Int = 0
    private var lastRx: Int = 0
    private var lastRy: Int = 0
    private var lastLt: Int = 0
    private var lastRt: Int = 0

    override fun onListen(arguments: Any?, events: EventChannel.EventSink?) {
        eventSink = events
    }

    override fun onCancel(arguments: Any?) {
        eventSink = null
    }

    fun emitSurfaceChanged(surfaceHandleId: Long) {
        eventSink?.success(
            mapOf(
                "kind" to "surface_changed",
                "surfaceHandleId" to surfaceHandleId
            )
        )
    }

    fun handleCapturedPointerEvent(event: MotionEvent): Boolean {
        val sink = eventSink ?: return false
        when (event.actionMasked) {
            MotionEvent.ACTION_MOVE, MotionEvent.ACTION_HOVER_MOVE -> {
                val dx = event.x
                val dy = event.y
                if (dx != 0f || dy != 0f) {
                    sink.success(
                        mapOf(
                            "kind" to "pointer_delta",
                            "dx" to dx.toDouble(),
                            "dy" to dy.toDouble()
                        )
                    )
                }
                emitButtonDiff(event.buttonState)
            }
            MotionEvent.ACTION_BUTTON_PRESS, MotionEvent.ACTION_BUTTON_RELEASE,
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_UP -> {
                emitButtonDiff(event.buttonState)
            }
            MotionEvent.ACTION_SCROLL -> {
                val v = (event.getAxisValue(MotionEvent.AXIS_VSCROLL) * 120f).roundToInt()
                val h = (event.getAxisValue(MotionEvent.AXIS_HSCROLL) * 120f).roundToInt()
                if (v != 0 || h != 0) {
                    sink.success(
                        mapOf(
                            "kind" to "pointer_wheel",
                            "dx" to h,
                            "dy" to v
                        )
                    )
                }
            }
        }
        return true
    }

    private fun emitButtonDiff(newState: Int) {
        val sink = eventSink ?: return
        val diff = newState xor lastMouseButtons
        if (diff == 0) return

        fun checkBtn(androidMask: Int, osmButton: Int) {
            if ((diff and androidMask) != 0) {
                val isDown = (newState and androidMask) != 0
                sink.success(
                    mapOf(
                        "kind" to "pointer_button",
                        "button" to osmButton,
                        "isDown" to isDown
                    )
                )
            }
        }

        // InputMouseButton ids: 1 left, 2 middle, 3 right.
        checkBtn(MotionEvent.BUTTON_PRIMARY, 1)
        checkBtn(MotionEvent.BUTTON_SECONDARY, 3)
        checkBtn(MotionEvent.BUTTON_TERTIARY, 2)
        checkBtn(MotionEvent.BUTTON_BACK, 4)
        checkBtn(MotionEvent.BUTTON_FORWARD, 5)
        lastMouseButtons = newState
    }

    fun handleGamepadKeyEvent(event: KeyEvent): Boolean {
        val source = event.source
        if ((source and InputDevice.SOURCE_GAMEPAD) != InputDevice.SOURCE_GAMEPAD &&
            (source and InputDevice.SOURCE_DPAD) != InputDevice.SOURCE_DPAD) {
            return false
        }
        val bit = when (event.keyCode) {
            KeyEvent.KEYCODE_DPAD_UP -> 0x0001
            KeyEvent.KEYCODE_DPAD_DOWN -> 0x0002
            KeyEvent.KEYCODE_DPAD_LEFT -> 0x0004
            KeyEvent.KEYCODE_DPAD_RIGHT -> 0x0008
            KeyEvent.KEYCODE_BUTTON_START -> 0x0010
            KeyEvent.KEYCODE_BUTTON_SELECT -> 0x0020
            KeyEvent.KEYCODE_BUTTON_THUMBL -> 0x0040
            KeyEvent.KEYCODE_BUTTON_THUMBR -> 0x0080
            KeyEvent.KEYCODE_BUTTON_L1 -> 0x0100
            KeyEvent.KEYCODE_BUTTON_R1 -> 0x0200
            KeyEvent.KEYCODE_BUTTON_A -> 0x1000
            KeyEvent.KEYCODE_BUTTON_B -> 0x2000
            KeyEvent.KEYCODE_BUTTON_X -> 0x4000
            KeyEvent.KEYCODE_BUTTON_Y -> 0x8000
            else -> return false
        }
        if (event.repeatCount > 0) return true

        gamepadButtonsMask = if (event.action == KeyEvent.ACTION_DOWN) {
            gamepadButtonsMask or bit
        } else {
            gamepadButtonsMask and bit.inv()
        }
        emitGamepadState()
        return true
    }

    fun handleGamepadMotionEvent(event: MotionEvent): Boolean {
        if ((event.source and InputDevice.SOURCE_JOYSTICK) != InputDevice.SOURCE_JOYSTICK ||
            event.action != MotionEvent.ACTION_MOVE) {
            return false
        }
        fun normStick(v: Float): Int {
            val clean = if (abs(v) < 0.08f) 0f else v.coerceIn(-1f, 1f)
            return (clean * 32767f).roundToInt()
        }

        lastLx = normStick(event.getAxisValue(MotionEvent.AXIS_X))
        lastLy = normStick(-event.getAxisValue(MotionEvent.AXIS_Y))
        lastRx = normStick(event.getAxisValue(MotionEvent.AXIS_Z))
        lastRy = normStick(-event.getAxisValue(MotionEvent.AXIS_RZ))
        lastLt = (event.getAxisValue(MotionEvent.AXIS_LTRIGGER).coerceIn(0f, 1f) * 255f).roundToInt()
        lastRt = (event.getAxisValue(MotionEvent.AXIS_RTRIGGER).coerceIn(0f, 1f) * 255f).roundToInt()

        val hatX = event.getAxisValue(MotionEvent.AXIS_HAT_X)
        val hatY = event.getAxisValue(MotionEvent.AXIS_HAT_Y)
        gamepadButtonsMask = gamepadButtonsMask and 0x000F.inv()
        if (hatY < -0.5f) gamepadButtonsMask = gamepadButtonsMask or 0x0001
        if (hatY > 0.5f) gamepadButtonsMask = gamepadButtonsMask or 0x0002
        if (hatX < -0.5f) gamepadButtonsMask = gamepadButtonsMask or 0x0004
        if (hatX > 0.5f) gamepadButtonsMask = gamepadButtonsMask or 0x0008

        emitGamepadState()
        return true
    }

    private fun emitGamepadState() {
        eventSink?.success(
            mapOf(
                "kind" to "gamepad",
                "controllerIndex" to 0,
                "connected" to 1,
                "buttonsMask" to gamepadButtonsMask,
                "leftTrigger" to lastLt,
                "rightTrigger" to lastRt,
                "leftThumbX" to lastLx,
                "leftThumbY" to lastLy,
                "rightThumbX" to lastRx,
                "rightThumbY" to lastRy
            )
        )
    }
}
