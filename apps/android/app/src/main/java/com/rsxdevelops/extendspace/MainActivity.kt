package com.rsxdevelops.extendspace

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import com.rsxdevelops.extendspace.ui.ExtendSpaceApp
import com.rsxdevelops.extendspace.ui.theme.ExtendSpaceTheme

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            ExtendSpaceTheme {
                ExtendSpaceApp()
            }
        }
    }
}
