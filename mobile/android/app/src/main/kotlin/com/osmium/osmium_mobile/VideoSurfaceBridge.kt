package com.osmium.osmium_mobile

import android.view.Surface
import io.flutter.view.TextureRegistry

class VideoSurfaceBridge(
    private val textureRegistry: TextureRegistry,
    private val onSurfaceHandleChanged: (Long) -> Unit
) {
    private var producer: TextureRegistry.SurfaceProducer? = null
    private var currentSurfaceHandle: Long = 0L

    companion object {
        init {
            System.loadLibrary("osmium_mobile_engine")
        }
    }

    private external fun nativeRegisterSurface(surface: Surface): Long
    private external fun nativeUnregisterSurface(handleId: Long)

    fun createSurface(width: Int, height: Int): Map<String, Long> {
        destroySurface()
        val prod = textureRegistry.createSurfaceProducer()
        prod.setSize(width.coerceAtLeast(16), height.coerceAtLeast(16))
        prod.setCallback(object : TextureRegistry.SurfaceProducer.Callback {
            override fun onSurfaceAvailable() {
                rebindSurface(prod)
            }

            override fun onSurfaceCleanup() {
                releaseNativeHandle()
                onSurfaceHandleChanged(0L)
            }
        })
        producer = prod
        rebindSurface(prod)
        return mapOf(
            "textureId" to prod.id(),
            "surfaceHandleId" to currentSurfaceHandle
        )
    }

    private fun rebindSurface(prod: TextureRegistry.SurfaceProducer) {
        releaseNativeHandle()
        val surf = prod.surface
        if (surf.isValid) {
            currentSurfaceHandle = nativeRegisterSurface(surf)
            onSurfaceHandleChanged(currentSurfaceHandle)
        }
    }

    fun resizeSurface(width: Int, height: Int) {
        producer?.setSize(width.coerceAtLeast(16), height.coerceAtLeast(16))
    }

    private fun releaseNativeHandle() {
        val h = currentSurfaceHandle
        currentSurfaceHandle = 0L
        if (h != 0L) {
            nativeUnregisterSurface(h)
        }
    }

    fun destroySurface() {
        releaseNativeHandle()
        producer?.release()
        producer = null
    }
}
