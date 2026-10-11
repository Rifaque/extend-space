package com.rsxdevelops.extendspace.ui.connect

import androidx.annotation.StringRes
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedCard
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.rsxdevelops.extendspace.R
import com.rsxdevelops.extendspace.connection.ConnectionErrorCode
import com.rsxdevelops.extendspace.connection.ConnectionState
import com.rsxdevelops.extendspace.connection.HostDevice
import com.rsxdevelops.extendspace.connection.TransportKind
import com.rsxdevelops.extendspace.ui.theme.ExtendSpaceTheme

@Composable
fun ConnectRoute(viewModel: ConnectViewModel = viewModel(factory = ConnectViewModel.Factory)) {
    val uiState by viewModel.uiState.collectAsStateWithLifecycle()
    ConnectScreen(uiState)
}

@Composable
fun ConnectScreen(uiState: ConnectUiState, modifier: Modifier = Modifier) {
    LazyColumn(
        modifier = modifier.fillMaxSize(),
        contentPadding = PaddingValues(horizontal = 20.dp, vertical = 24.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item {
            Column(modifier = Modifier.padding(bottom = 8.dp)) {
                Text(
                    text = stringResource(R.string.connect_title),
                    style = MaterialTheme.typography.headlineMedium,
                )
                Text(
                    text = stringResource(R.string.connect_subtitle),
                    style = MaterialTheme.typography.bodyLarge,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }

        item { ConnectionStatusCard(uiState.connection) }

        item { SectionHeader(R.string.hosts_title) }
        if (uiState.hosts.isEmpty()) {
            item { HostsEmptyState() }
        } else {
            items(uiState.hosts, key = { it.id }) { host -> HostRow(host) }
        }

        item { SectionHeader(R.string.methods_title) }
        item {
            OutlinedCard(modifier = Modifier.fillMaxWidth()) {
                ConnectionMethodRow(R.string.method_usb_title, R.string.method_usb_detail)
                HorizontalDivider()
                ConnectionMethodRow(R.string.method_wireless_title, R.string.method_wireless_detail)
            }
        }
    }
}

@Composable
private fun ConnectionStatusCard(state: ConnectionState) {
    val status = state.toStatus()
    ElevatedCard(
        modifier = Modifier.fillMaxWidth(),
        colors = CardDefaults.elevatedCardColors(
            containerColor = MaterialTheme.colorScheme.surfaceContainerHigh,
        ),
    ) {
        Row(
            modifier = Modifier.padding(20.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Box(
                modifier = Modifier
                    .size(12.dp)
                    .background(status.indicator, CircleShape),
            )
            Column {
                Text(
                    text = stringResource(status.title),
                    style = MaterialTheme.typography.titleLarge,
                )
                Text(
                    text = stringResource(status.detail),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
        }
    }
}

private class Status(@StringRes val title: Int, @StringRes val detail: Int, val indicator: Color)

@Composable
private fun ConnectionState.toStatus(): Status {
    val colors = MaterialTheme.colorScheme
    return when (this) {
        ConnectionState.Disconnected ->
            Status(R.string.state_disconnected, R.string.state_disconnected_detail, colors.outline)
        is ConnectionState.Connecting ->
            Status(R.string.state_connecting, transport.detail(), colors.tertiary)
        is ConnectionState.Negotiating ->
            Status(R.string.state_negotiating, transport.detail(), colors.tertiary)
        is ConnectionState.Streaming ->
            Status(R.string.state_streaming, transport.detail(), colors.primary)
        is ConnectionState.Reconnecting ->
            Status(R.string.state_reconnecting, transport.detail(), colors.tertiary)
        ConnectionState.Disconnecting ->
            Status(R.string.state_disconnecting, R.string.state_disconnected_detail, colors.outline)
        is ConnectionState.Failed ->
            Status(R.string.state_failed, error.code.message(), colors.error)
    }
}

@StringRes
private fun TransportKind.detail(): Int = when (this) {
    TransportKind.USB -> R.string.state_via_usb
    TransportKind.WIRELESS -> R.string.state_via_wireless
}

@StringRes
private fun ConnectionErrorCode.message(): Int = when (this) {
    ConnectionErrorCode.TRANSPORT_UNAVAILABLE -> R.string.error_transport_unavailable
    ConnectionErrorCode.AUTHENTICATION_FAILED -> R.string.error_authentication_failed
    ConnectionErrorCode.INCOMPATIBLE_PROTOCOL -> R.string.error_incompatible_protocol
    ConnectionErrorCode.NEGOTIATION_FAILED -> R.string.error_negotiation_failed
    ConnectionErrorCode.CONNECTION_LOST -> R.string.error_connection_lost
    ConnectionErrorCode.INTERNAL -> R.string.error_internal
}

@Composable
private fun SectionHeader(@StringRes title: Int) {
    Text(
        text = stringResource(title),
        style = MaterialTheme.typography.titleSmall,
        color = MaterialTheme.colorScheme.primary,
        modifier = Modifier.padding(top = 12.dp),
    )
}

@Composable
private fun HostsEmptyState() {
    OutlinedCard(modifier = Modifier.fillMaxWidth()) {
        Column(
            modifier = Modifier.padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            Text(
                text = stringResource(R.string.hosts_empty_title),
                style = MaterialTheme.typography.titleMedium,
            )
            Text(
                text = stringResource(R.string.hosts_empty_detail),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun HostRow(host: HostDevice) {
    OutlinedCard(modifier = Modifier.fillMaxWidth()) {
        Column(modifier = Modifier.padding(horizontal = 20.dp, vertical = 16.dp)) {
            Text(text = host.displayName, style = MaterialTheme.typography.titleMedium)
            Text(
                text = stringResource(
                    when (host.transport) {
                        TransportKind.USB -> R.string.host_transport_usb
                        TransportKind.WIRELESS -> R.string.host_transport_wireless
                    },
                ),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

@Composable
private fun ConnectionMethodRow(@StringRes title: Int, @StringRes detail: Int) {
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .padding(horizontal = 20.dp, vertical = 16.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(text = stringResource(title), style = MaterialTheme.typography.titleMedium)
            Text(
                text = stringResource(detail),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        NotImplementedBadge()
    }
}

@Composable
fun NotImplementedBadge(modifier: Modifier = Modifier) {
    Surface(
        modifier = modifier,
        shape = MaterialTheme.shapes.small,
        color = MaterialTheme.colorScheme.surfaceContainerHighest,
    ) {
        Text(
            text = stringResource(R.string.badge_not_implemented),
            style = MaterialTheme.typography.labelMedium,
            color = MaterialTheme.colorScheme.onSurfaceVariant,
            modifier = Modifier.padding(horizontal = 8.dp, vertical = 4.dp),
        )
    }
}

@Preview(showBackground = true)
@Composable
private fun ConnectScreenPreview() {
    ExtendSpaceTheme {
        ConnectScreen(ConnectUiState())
    }
}
