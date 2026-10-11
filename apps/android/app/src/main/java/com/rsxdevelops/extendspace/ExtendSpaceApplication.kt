package com.rsxdevelops.extendspace

import android.app.Application
import com.rsxdevelops.extendspace.connection.ConnectionController
import com.rsxdevelops.extendspace.connection.IdleConnectionController

class ExtendSpaceApplication : Application() {
    /** Process-wide so a session can outlive any single screen. */
    val connectionController: ConnectionController = IdleConnectionController()
}
