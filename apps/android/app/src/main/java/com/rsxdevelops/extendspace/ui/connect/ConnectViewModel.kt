package com.rsxdevelops.extendspace.ui.connect

import androidx.lifecycle.ViewModel
import androidx.lifecycle.ViewModelProvider.AndroidViewModelFactory.Companion.APPLICATION_KEY
import androidx.lifecycle.viewModelScope
import androidx.lifecycle.viewmodel.initializer
import androidx.lifecycle.viewmodel.viewModelFactory
import com.rsxdevelops.extendspace.ExtendSpaceApplication
import com.rsxdevelops.extendspace.connection.ConnectionController
import com.rsxdevelops.extendspace.connection.ConnectionState
import com.rsxdevelops.extendspace.connection.HostDevice
import kotlinx.coroutines.flow.SharingStarted
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.combine
import kotlinx.coroutines.flow.stateIn

data class ConnectUiState(
    val connection: ConnectionState = ConnectionState.Disconnected,
    val hosts: List<HostDevice> = emptyList(),
)

class ConnectViewModel(controller: ConnectionController) : ViewModel() {
    val uiState: StateFlow<ConnectUiState> =
        combine(controller.state, controller.hosts, ::ConnectUiState).stateIn(
            scope = viewModelScope,
            started = SharingStarted.WhileSubscribed(5_000),
            initialValue = ConnectUiState(controller.state.value, controller.hosts.value),
        )

    companion object {
        val Factory = viewModelFactory {
            initializer {
                val application = this[APPLICATION_KEY] as ExtendSpaceApplication
                ConnectViewModel(application.connectionController)
            }
        }
    }
}
