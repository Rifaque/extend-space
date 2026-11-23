package com.rsxdevelops.extendspace.ui.session

import com.rsxdevelops.extendspace.connection.TouchContactSample
import kotlin.math.roundToInt

/** What a touch event says happened, as `MotionEvent`'s masked action does. */
enum class TouchAction { DOWN, POINTER_DOWN, MOVE, POINTER_UP, UP, CANCEL }

/** One pointer of a touch event, in the session surface's own pixels. */
data class PointerSample(val id: Int, val x: Float, val y: Float)

/**
 * Turns the session surface's touch events into touch frames (D81, D83, D84). Pure: the screen feeds it
 * what each `MotionEvent` says, so that it is tested on the desktop JVM.
 *
 * The surface is laid out to exactly the picture (D69), so a position divided by the surface's size is
 * the position on the picture, whatever the letterbox, rotation or resolution. Each frame lists every
 * contact that is down plus any lifting in it. At most 10 contacts are followed; others are ignored
 * for their whole life. [cancelAll] lifts every contact, for when the gesture cannot finish normally.
 */
class TouchCapture(private val send: (sequence: Long, contacts: List<TouchContactSample>) -> Boolean) {
    private val active = LinkedHashMap<Int, Pair<Int, Int>>()
    private var sequence = 0L
    private var lastSent: List<TouchContactSample>? = null

    /** Contacts currently down, as the PC was told. */
    val activeCount: Int get() = active.size

    fun onEvent(action: TouchAction, actionPointerId: Int, pointers: List<PointerSample>, width: Int, height: Int) {
        if (action == TouchAction.CANCEL) {
            cancelAll()
            return
        }
        if (width <= 0 || height <= 0) return

        val contacts = mutableListOf<TouchContactSample>()
        for (pointer in pointers) {
            val position = normalise(pointer, width, height)
            val isActionPointer = pointer.id == actionPointerId
            val phase = when {
                (action == TouchAction.DOWN || action == TouchAction.POINTER_DOWN) && isActionPointer -> {
                    val known = pointer.id in active
                    if (!known && (pointer.id !in 0..MAX_ID || active.size >= MAX_CONTACTS)) continue
                    active[pointer.id] = position
                    if (known) PHASE_MOVE else PHASE_DOWN
                }
                (action == TouchAction.UP || action == TouchAction.POINTER_UP) && isActionPointer -> {
                    if (active.remove(pointer.id) == null) continue
                    PHASE_UP
                }
                else -> {
                    if (pointer.id !in active) continue
                    active[pointer.id] = position
                    PHASE_MOVE
                }
            }
            contacts += TouchContactSample(pointer.id, phase, position.first, position.second)
        }
        emit(contacts)
    }

    /** Lifts every contact as cancelled: the surface went away, the screen paused, or the system took the gesture. */
    fun cancelAll() {
        if (active.isEmpty()) return
        val contacts = active.map { (id, position) -> TouchContactSample(id, PHASE_CANCEL, position.first, position.second) }
        active.clear()
        emit(contacts)
    }

    /** A new stream: its frames are numbered from zero again, and nothing of the last one is held. */
    fun reset() {
        active.clear()
        sequence = 0
        lastSent = null
    }

    private fun emit(contacts: List<TouchContactSample>) {
        if (contacts.isEmpty()) return
        // Android reports moves of a still finger; a frame identical to the last says nothing new.
        if (contacts.all { it.phase == PHASE_MOVE } && contacts == lastSent) return
        if (send(sequence and 0xFFFF_FFFFL, contacts)) {
            sequence++
            lastSent = contacts
        }
    }

    private fun normalise(pointer: PointerSample, width: Int, height: Int): Pair<Int, Int> = Pair(
        (pointer.x / width * FULL).roundToInt().coerceIn(0, FULL),
        (pointer.y / height * FULL).roundToInt().coerceIn(0, FULL),
    )

    private companion object {
        const val FULL = 65_535
        const val MAX_CONTACTS = 10
        const val MAX_ID = 31
        const val PHASE_DOWN = 0
        const val PHASE_MOVE = 1
        const val PHASE_UP = 2
        const val PHASE_CANCEL = 3
    }
}
