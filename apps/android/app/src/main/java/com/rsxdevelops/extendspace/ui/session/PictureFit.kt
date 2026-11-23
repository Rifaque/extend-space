package com.rsxdevelops.extendspace.ui.session

import kotlin.math.roundToInt

/** A size in pixels. */
data class PixelSize(val width: Int, val height: Int)

/**
 * The largest size with the picture's shape that fits inside the screen area (D69): the picture is
 * never stretched or cropped; the rest of the area is left black (letterbox above and below, or
 * pillarbox at the sides). One side always fills the area exactly. A picture of unknown or invalid
 * size is treated as 16:9, the only shape the host's default policy streams (D16).
 */
fun fitPicture(areaWidth: Int, areaHeight: Int, pictureWidth: Int, pictureHeight: Int): PixelSize {
    if (areaWidth <= 0 || areaHeight <= 0) return PixelSize(0, 0)
    val (w, h) = if (pictureWidth > 0 && pictureHeight > 0) pictureWidth to pictureHeight else DEFAULT_WIDTH to DEFAULT_HEIGHT

    // Compare areaWidth / areaHeight with w / h without rounding.
    return if (areaWidth.toLong() * h <= areaHeight.toLong() * w) {
        PixelSize(areaWidth, (areaWidth.toLong() * h / w.toDouble()).roundToInt().coerceIn(1, areaHeight))
    } else {
        PixelSize((areaHeight.toLong() * w / h.toDouble()).roundToInt().coerceIn(1, areaWidth), areaHeight)
    }
}

private const val DEFAULT_WIDTH = 16
private const val DEFAULT_HEIGHT = 9
