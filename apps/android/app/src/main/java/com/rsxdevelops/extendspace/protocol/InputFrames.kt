package com.rsxdevelops.extendspace.protocol

/** Contact phases of a `touch` frame (input architecture section 2). */
internal object TouchPhase {
    const val DOWN = 0
    const val MOVE = 1
    const val UP = 2
    const val CANCEL = 3
}

/** One contact: [x] and [y] are 0..65535 over the picture. */
internal data class TouchContact(val id: Int, val phase: Int, val x: Int, val y: Int)

/**
 * A complete input-channel frame, ready to write: sent by the link's one writer as it is, not
 * through [ControlCodec]. A [ControlMessage] only so that it can share the writer's queue.
 */
internal class InputFrame(val bytes: ByteArray) : ControlMessage

internal object InputFrames {
    const val TOUCH_TYPE = 1
    const val MAX_CONTACTS = 10
    const val MAX_CONTACT_ID = 31
    private const val HEADER = 8
    private const val CONTACT = 8

    /** The `touch` payload: sequence, count, then 8 bytes per contact (D81). */
    fun touchPayload(sequence: Long, contacts: List<TouchContact>): ByteArray {
        require(contacts.size in 1..MAX_CONTACTS) { "A touch frame carries 1 to $MAX_CONTACTS contacts." }
        val payload = ByteArray(HEADER + contacts.size * CONTACT)
        putInt(payload, 0, sequence)
        payload[4] = contacts.size.toByte()
        contacts.forEachIndexed { index, contact ->
            require(contact.id in 0..MAX_CONTACT_ID && contact.phase in TouchPhase.DOWN..TouchPhase.CANCEL)
            val at = HEADER + index * CONTACT
            payload[at] = contact.id.toByte()
            payload[at + 1] = contact.phase.toByte()
            putShort(payload, at + 2, contact.x.coerceIn(0, 65_535))
            putShort(payload, at + 4, contact.y.coerceIn(0, 65_535))
        }
        return payload
    }

    /** Header and payload, for the writer. */
    fun touchFrame(sequence: Long, contacts: List<TouchContact>): InputFrame {
        val payload = touchPayload(sequence, contacts)
        val frame = ByteArray(Protocol.HEADER_LENGTH + payload.size)
        FrameHeader(Protocol.CHANNEL_INPUT, TOUCH_TYPE, 0, payload.size.toLong()).write(frame)
        System.arraycopy(payload, 0, frame, Protocol.HEADER_LENGTH, payload.size)
        return InputFrame(frame)
    }

    private fun putInt(target: ByteArray, at: Int, value: Long) {
        target[at] = (value ushr 24).toByte()
        target[at + 1] = (value ushr 16).toByte()
        target[at + 2] = (value ushr 8).toByte()
        target[at + 3] = value.toByte()
    }

    private fun putShort(target: ByteArray, at: Int, value: Int) {
        target[at] = (value ushr 8).toByte()
        target[at + 1] = value.toByte()
    }
}
