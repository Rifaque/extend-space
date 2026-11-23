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
import androidx.compose.material3.Button
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.ElevatedCard
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedCard
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.ui.text.style.TextAlign
import com.rsxdevelops.extendspace.wireless.DeviceIds
import com.rsxdevelops.extendspace.wireless.PairingCode
import com.rsxdevelops.extendspace.wireless.PairingState
import com.rsxdevelops.extendspace.wireless.TrustedHost
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.tooling.preview.Preview
import androidx.compose.ui.unit.dp
import androidx.lifecycle.compose.collectAsStateWithLifecycle
import androidx.lifecycle.viewmodel.compose.viewModel
import com.rsxdevelops.extendspace.R
import com.rsxdevelops.extendspace.connection.ConnectionState
import com.rsxdevelops.extendspace.connection.HostDevice
import com.rsxdevelops.extendspace.connection.TransportKind
import com.rsxdevelops.extendspace.connection.WaitResult
import com.rsxdevelops.extendspace.ui.session.SessionActivity
import com.rsxdevelops.extendspace.ui.session.isActive
import com.rsxdevelops.extendspace.ui.session.message
import com.rsxdevelops.extendspace.ui.theme.ExtendSpaceTheme

@Composable
fun ConnectRoute(viewModel: ConnectViewModel = viewModel(factory = ConnectViewModel.Factory)) {
    val uiState by viewModel.uiState.collectAsStateWithLifecycle()
    val context = LocalContext.current
    var notice by remember { mutableStateOf<Int?>(null) }
    ConnectScreen(
        uiState = uiState,
        notice = notice,
        onWaitForPc = {
            when (viewModel.waitForPc()) {
                WaitResult.STARTED, WaitResult.ALREADY_ACTIVE -> {
                    notice = null
                    context.startActivity(SessionActivity.intent(context))
                }
                WaitResult.PREVIOUS_SESSION_NOT_RELEASED -> notice = R.string.wait_previous_not_released
            }
        },
        onOpenSession = { context.startActivity(SessionActivity.intent(context)) },
        onWifiChanged = viewModel::setWifiEnabled,
        onPair = { viewModel.openPairing() },
        onPairingDecision = { accepted -> viewModel.confirmPairing(accepted) },
        onPairingCancel = { viewModel.closePairing() },
        onPairingDone = { viewModel.acknowledgePairing() },
        onForget = { host -> viewModel.forget(host) },
    )
}

@Composable
fun ConnectScreen(
    uiState: ConnectUiState,
    modifier: Modifier = Modifier,
    @StringRes notice: Int? = null,
    onWaitForPc: () -> Unit = {},
    onOpenSession: () -> Unit = {},
    onWifiChanged: (Boolean) -> Unit = {},
    onPair: () -> Unit = {},
    onPairingDecision: (Boolean) -> Unit = {},
    onPairingCancel: () -> Unit = {},
    onPairingDone: () -> Unit = {},
    onForget: (TrustedHost) -> Unit = {},
) {
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

        item { ConnectionStatusCard(uiState.connection, uiState.connectedOver, notice, onWaitForPc, onOpenSession) }

        if (uiState.pairing !is PairingState.Closed) {
            item { PairingCard(uiState.pairing, uiState.tabletId, uiState.tabletAddress, onPairingDecision, onPairingCancel, onPairingDone) }
        }

        item { HowToConnectCard() }

        // Only wireless hosts would be listed, and wireless is not built: the section appears once there are any.
        if (uiState.hosts.isNotEmpty()) {
            item { SectionHeader(R.string.hosts_title) }
            items(uiState.hosts, key = { it.id }) { host -> HostRow(host) }
        }

        item { SectionHeader(R.string.methods_title) }
        item {
            OutlinedCard(modifier = Modifier.fillMaxWidth()) {
                ConnectionMethodRow(R.string.method_usb_title, R.string.method_usb_detail, implemented = true)
                HorizontalDivider()
                if (uiState.wirelessAvailable) {
                    WifiMethodRow(uiState.wifiEnabled, uiState.tabletAddress, pairingIdle = uiState.pairing is PairingState.Closed, onWifiChanged, onPair)
                } else {
                    ConnectionMethodRow(R.string.method_wireless_title, R.string.method_wireless_detail, implemented = false)
                }
            }
        }

        if (uiState.pairedPcs.isNotEmpty()) {
            item { SectionHeader(R.string.paired_title) }
            items(uiState.pairedPcs, key = { it.deviceId }) { host -> PairedPcRow(host, onForget) }
        }
    }
}

@Composable
private fun WifiMethodRow(enabled: Boolean, address: String?, pairingIdle: Boolean, onWifiChanged: (Boolean) -> Unit, onPair: () -> Unit) {
    Column(modifier = Modifier.fillMaxWidth().padding(horizontal = 20.dp, vertical = 16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(12.dp)) {
            Column(modifier = Modifier.weight(1f)) {
                Text(text = stringResource(R.string.method_wireless_switch), style = MaterialTheme.typography.titleMedium)
                Text(
                    text = stringResource(if (enabled) R.string.method_wireless_detail_on else R.string.method_wireless_detail_off),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            Switch(checked = enabled, onCheckedChange = onWifiChanged)
        }
        if (enabled && address != null) {
            Text(
                text = stringResource(R.string.method_wireless_address, address),
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
        if (enabled && pairingIdle) {
            OutlinedButton(onClick = onPair) { Text(stringResource(R.string.pair_action)) }
        }
    }
}

/** D77: the tablet's side of pairing. The code is shown large; the user confirms only if the PC shows the same. */
@Composable
private fun PairingCard(
    pairing: PairingState,
    tabletId: String?,
    tabletAddress: String?,
    onDecision: (Boolean) -> Unit,
    onCancel: () -> Unit,
    onDone: () -> Unit,
) {
    ElevatedCard(modifier = Modifier.fillMaxWidth()) {
        Column(modifier = Modifier.padding(20.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
            when (pairing) {
                is PairingState.Waiting -> {
                    Text(text = stringResource(R.string.pair_waiting_title), style = MaterialTheme.typography.titleLarge)
                    Text(text = stringResource(R.string.pair_waiting_detail, pairing.deviceName), style = MaterialTheme.typography.bodyMedium)
                    if (tabletId != null) {
                        Text(
                            text = if (tabletAddress != null) {
                                stringResource(R.string.pair_this_tablet_id_address, tabletId, tabletAddress)
                            } else {
                                stringResource(R.string.pair_this_tablet_id, tabletId)
                            },
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                    OutlinedButton(onClick = onCancel) { Text(stringResource(R.string.pair_cancel)) }
                }
                is PairingState.Confirm -> {
                    Text(text = stringResource(R.string.pair_confirm_title, pairing.hostName), style = MaterialTheme.typography.titleLarge)
                    Text(
                        text = PairingCode.format(pairing.code),
                        style = MaterialTheme.typography.displayMedium,
                        modifier = Modifier.fillMaxWidth(),
                        textAlign = TextAlign.Center,
                    )
                    Text(text = stringResource(R.string.pair_confirm_detail), style = MaterialTheme.typography.bodyMedium)
                    Row(horizontalArrangement = Arrangement.spacedBy(12.dp)) {
                        Button(onClick = { onDecision(true) }) { Text(stringResource(R.string.pair_confirm_accept)) }
                        OutlinedButton(onClick = { onDecision(false) }) { Text(stringResource(R.string.pair_cancel)) }
                    }
                }
                is PairingState.Finished -> {
                    Text(
                        text = pairing.message,
                        style = MaterialTheme.typography.titleMedium,
                        color = if (pairing.paired) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.error,
                    )
                    Button(onClick = onDone) { Text(stringResource(R.string.pair_done)) }
                }
                PairingState.Closed -> Unit
            }
        }
    }
}

@Composable
private fun PairedPcRow(host: TrustedHost, onForget: (TrustedHost) -> Unit) {
    OutlinedCard(modifier = Modifier.fillMaxWidth()) {
        Row(
            modifier = Modifier.padding(horizontal = 20.dp, vertical = 12.dp),
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Column(modifier = Modifier.weight(1f)) {
                Text(text = host.name, style = MaterialTheme.typography.titleMedium)
                Text(
                    text = stringResource(R.string.paired_detail, DeviceIds.short(host.deviceId)),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
            }
            TextButton(onClick = { onForget(host) }) { Text(stringResource(R.string.paired_forget)) }
        }
    }
}

@Composable
private fun ConnectionStatusCard(
    state: ConnectionState,
    connectedOver: TransportKind?,
    @StringRes notice: Int?,
    onWaitForPc: () -> Unit,
    onOpenSession: () -> Unit,
) {
    val status = state.toStatus(connectedOver)
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
            Column(modifier = Modifier.weight(1f)) {
                Text(
                    text = stringResource(status.title),
                    style = MaterialTheme.typography.titleLarge,
                )
                Text(
                    text = stringResource(status.detail),
                    style = MaterialTheme.typography.bodyMedium,
                    color = MaterialTheme.colorScheme.onSurfaceVariant,
                )
                if (notice != null) {
                    Text(
                        text = stringResource(notice),
                        style = MaterialTheme.typography.bodyMedium,
                        color = MaterialTheme.colorScheme.error,
                    )
                }
            }
            if (state.isActive || state is ConnectionState.Disconnecting) {
                Button(onClick = onOpenSession) { Text(stringResource(R.string.action_open_session)) }
            } else {
                Button(onClick = onWaitForPc) { Text(stringResource(R.string.action_wait_for_pc)) }
            }
        }
    }
}

@Composable
private fun HowToConnectCard() {
    OutlinedCard(modifier = Modifier.fillMaxWidth()) {
        Column(
            modifier = Modifier.padding(20.dp),
            verticalArrangement = Arrangement.spacedBy(4.dp),
        ) {
            Text(text = stringResource(R.string.connect_how_title), style = MaterialTheme.typography.titleMedium)
            Text(
                text = stringResource(R.string.connect_how_detail),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
        }
    }
}

private class Status(@StringRes val title: Int, @StringRes val detail: Int, val indicator: Color)

@Composable
private fun ConnectionState.toStatus(connectedOver: TransportKind?): Status {
    val colors = MaterialTheme.colorScheme
    return when (this) {
        ConnectionState.Disconnected ->
            Status(R.string.state_disconnected, R.string.state_disconnected_detail, colors.outline)
        is ConnectionState.Connecting ->
            Status(R.string.state_connecting, transport.detail(), colors.tertiary)
        is ConnectionState.Negotiating ->
            Status(R.string.state_negotiating, connectedOver.connectedDetail(transport), colors.tertiary)
        is ConnectionState.Streaming ->
            Status(R.string.state_streaming, connectedOver.connectedDetail(transport), colors.primary)
        is ConnectionState.Reconnecting ->
            Status(R.string.state_reconnecting, transport.detail(), colors.tertiary)
        ConnectionState.Disconnecting ->
            Status(R.string.state_disconnecting, R.string.state_disconnected_detail, colors.outline)
        is ConnectionState.Failed ->
            Status(R.string.state_failed, error.code.message(), colors.error)
    }
}

/** While waiting: a session asked for with Wi-Fi allowed listens on both routes (D74). */
@StringRes
private fun TransportKind.detail(): Int = when (this) {
    TransportKind.USB -> R.string.state_via_usb
    TransportKind.WIRELESS -> R.string.state_via_usb_or_wifi
}

/** Once a PC is connected: the route it actually came by. */
@StringRes
private fun TransportKind?.connectedDetail(requested: TransportKind): Int = when (this ?: requested) {
    TransportKind.USB -> R.string.state_via_usb
    TransportKind.WIRELESS -> if (this == null) R.string.state_via_usb_or_wifi else R.string.state_via_wifi
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
private fun ConnectionMethodRow(@StringRes title: Int, @StringRes detail: Int, implemented: Boolean) {
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
        if (!implemented) NotImplementedBadge()
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
