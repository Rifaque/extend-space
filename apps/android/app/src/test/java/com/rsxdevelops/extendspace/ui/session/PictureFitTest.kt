package com.rsxdevelops.extendspace.ui.session

import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test
import kotlin.math.abs

/** How the session screen sizes the picture (D69): never stretched, never cropped, centred by the screen. */
class PictureFitTest {
    @Test
    fun `a 16 by 9 stream fills a 16 by 9 screen exactly`() {
        assertEquals(PixelSize(1920, 1080), fitPicture(1920, 1080, 1920, 1080))
        assertEquals(PixelSize(2560, 1440), fitPicture(2560, 1440, 1280, 720))
    }

    @Test
    fun `on the Pad 7 landscape screen a 1080p stream is letterboxed above and below`() {
        // 3200 x 2136 is the Pad 7's display in landscape.
        assertEquals(PixelSize(3200, 1800), fitPicture(3200, 2136, 1920, 1080))
    }

    @Test
    fun `on a phone held upright the stream fills the width and is letterboxed`() {
        assertEquals(PixelSize(1080, 608), fitPicture(1080, 2400, 1920, 1080))
    }

    @Test
    fun `on a wide phone in landscape the stream fills the height and is pillarboxed`() {
        assertEquals(PixelSize(1920, 1080), fitPicture(2400, 1080, 1920, 1080))
    }

    @Test
    fun `a 4 by 3 or a portrait stream keeps its own shape`() {
        assertEquals(PixelSize(2848, 2136), fitPicture(3200, 2136, 1024, 768))
        assertEquals(PixelSize(1202, 2136), fitPicture(3200, 2136, 1080, 1920))
    }

    @Test
    fun `an unknown stream size is treated as 16 by 9 and an empty area gives nothing`() {
        assertEquals(PixelSize(3200, 1800), fitPicture(3200, 2136, 0, 0))
        assertEquals(PixelSize(0, 0), fitPicture(0, 1080, 1920, 1080))
    }

    @Test
    fun `for many shapes the result fits, fills one side, and keeps the aspect within a pixel`() {
        val areas = listOf(320 to 240, 1080 to 2400, 2400 to 1080, 3200 to 2136, 2136 to 3200, 1 to 1, 7 to 3)
        val pictures = listOf(1920 to 1080, 1280 to 720, 1024 to 768, 1080 to 1920, 3840 to 1600, 1 to 1000)
        for ((aw, ah) in areas) for ((pw, ph) in pictures) {
            val fit = fitPicture(aw, ah, pw, ph)
            assertTrue("$aw x $ah / $pw x $ph -> $fit", fit.width in 1..aw && fit.height in 1..ah)
            assertTrue("one side fills: $fit in $aw x $ah", fit.width == aw || fit.height == ah)
            if (fit.width > 1 && fit.height > 1) {
                val heightFromWidth = fit.width.toDouble() * ph / pw
                val widthFromHeight = fit.height.toDouble() * pw / ph
                assertTrue("aspect kept: $fit for $pw x $ph", abs(heightFromWidth - fit.height) <= 1.0 || abs(widthFromHeight - fit.width) <= 1.0)
            }
        }
    }
}
