//
//  PuckPeripheral.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 8/2/20.
//  Copyright © 2020 Daniel Radtke. All rights reserved.
//

import Foundation
import UIKit
import SwiftyBluetooth
import CoreBluetooth
import Combine

class PuckError: LocalizedError {
    var errorDescription: String? { return _description }
    
    private var _description: String
    
    init(description: String) {
        self._description = description
    }
}

extension NSNotification.Name {
    static let PuckPeripheralPucksChanged = Notification.Name("PuckPeripheral.PucksChanged")
}

/// PuckPeripheral structs
extension PuckPeripheral {
    struct TagStatus {
        let slot: UInt8
        let start: Int
        let count: Int
        let total: Int
    }
    
    struct SlotStatus {
        let current: UInt8
        let total: UInt8
    }
    
    struct SlotInfo {
        var slot: UInt8
        var name: String
        var idHex: String
        var dump: TagDump
    }
    struct BleProgress {
        var current: Int
        var total: Int
    }
    struct SendReceiveProgress {
        var sendProgress: BleProgress?
        var receiveProgress: BleProgress?
    }
    struct ClearResult {
        var slot: UInt8
        var uid: Data
    }
}

extension PuckPeripheral {
    /// Enum representing the various commands and sub-commands for the NTAG215 puck.
    enum Commands: UInt8 {
        
        // MARK: - Main Commands
        
        /// Tests BLE packet transmission.
        /// - Parameter bytes: An optional byte indicating the number of bytes to send back. If not specified, defaults to 255.
        /// - Returns: The number of bytes requested, or 255 if not specified.
        case blePacketTest = 0x00
        
        /// A dual-purpose command that can be used to get a subset of data for identifying the tag, or to get the current slot number and the total number of slots.
        /// - Parameters:
        ///   - slot: An optional byte indicating the slot number. If the slot is out of range, the current slot is used.
        ///   - count: An optional byte indicating the number of slots to read. This requires that the slot be specified.
        /// - Returns: The number of bytes requested, or 255 if not specified.
        case slotInformation = 0x01
        
        /// Reads data from a slot.
        /// - Parameters:
        ///   - slot: A byte indicating the slot number. If the slot is out of range, the current slot is used.
        ///   - startPage: A byte indicating the start page.
        ///   - pageCount: A byte indicating the number of pages to read.
        /// - Returns: One byte indicating the command, the slot number used, the start page, the page count, and then the data.
        ///            Total number of bytes: 4 + (pageCount * 4)
        case read = 0x02
        
        /// Writes data to a slot.
        /// - Parameters:
        ///   - slot: A byte indicating the slot number. If the slot is out of range, the current slot is used.
        ///   - startPage: A byte indicating the start page.
        ///   - data: The data to write.
        /// - Returns: Bytes indicating the command, the slot number used, and the start page.
        ///            Total number of bytes: 3
        case write = 0x03
        
        /// Saves the slot. If `SAVE_TO_FLASH` is true, this will save the slot to flash, otherwise it will do nothing.
        /// This always should be called after `COMMAND_WRITE` or `COMMAND_FULL_WRITE`.
        /// - Parameter slot: An optional byte indicating the slot number. If out of range, the current slot is used.
        /// - Returns: The initial command data sent. This can be ignored and is only sent to acknowledge the command.
        case save = 0x04
        
        /// Writes a full slot.
        /// The command should be sent as one BLE packet, then an acknowledgement will be sent back consisting of the command and the slot.
        /// After the acknowledgement, exactly 572 bytes should be sent.
        /// This command will wait until all data has been received before more commands can be executed.
        /// - Parameters:
        ///   - slot: The slot number to write to.
        ///   - crc32: The CRC32 checksum of the data encoded as four bytes in little-endian format. If not specified, the data won't be validated. If this is incorrect, the data will be rejected.
        /// - Returns: Bytes indicating the command, slot, and the CRC32 checksum of the received data encoded as four bytes in little-endian format.
        case fullWrite = 0x05
        
        /// Reads a full slot at a time.
        /// - Parameters:
        ///   - slot: An optional byte indicating the slot number. If not specified, the current slot is used.
        ///   - count: An optional byte indicating the number of slots to read. This requires that the slot be specified.
        /// - Returns: The command, slot number, CRC32 checksum of the data encoded as four bytes in little-endian format, and 572 bytes of tag data. This will be repeated for the number of slots requested.
        case fullRead = 0x06
        
        /// A command with sub-commands to manage the slots with amiitool.
        /// If no sub-command is specified, the available commands will be returned.
        /// - Parameter subCommand: An optional byte indicating the sub-command.
        /// - Returns: The available sub-commands if no sub-command is specified.
        case amiitool = 0xEE
        
        /// Sets the slot to a blank NTAG215 with a random UID.
        /// - Parameter slot: A byte indicating the slot number.
        /// - Returns: The command, slot, and the nine byte UID of the generated tag.
        ///            Total number of bytes: 11
        case clearSlot = 0xF9
        
        /// Requests the Bluetooth name.
        /// - Returns: The name, and a null terminator.
        case getBluetoothName = 0xFA
        
        /// Sets the Bluetooth name.
        /// - Parameter name: Expects the name bytes followed by a null terminator.
        /// - Returns: The command data sent.
        case setBluetoothName = 0xFB
        
        /// Requests the firmware name.
        /// - Returns: The firmware name, and a null terminator.
        case getFirmware = 0xFC
        
        /// Moves one slot to another slot.
        /// - Parameters:
        ///   - from: A byte indicating the slot to move from.
        ///   - to: A byte indicating the slot to move to.
        case moveSlot = 0xFD
        
        /// Immediately enables the BLE UART console.
        /// Any data received after should be processed as the espruino console.
        case enableBleUart = 0xFE
        
        /// Restarts NFC.
        /// - Parameter slot: An optional byte indicating the slot number. If the slot is out of range, the current slot is used.
        /// - Returns: The command byte and the slot used.
        case restartNfc = 0xFF
        
        // MARK: - Amiitool Sub-Commands
        
        /// Enum representing the sub-commands for the AMIITOOL command.
        enum AmiitoolSubCommand: UInt8 {
            /// A sub-command to check if the amiitool key is set as well as to set it.
            /// If no additional data is sent, this will return 0x00 if the key is present, or 0x01 if it is not.
            /// To set the key, send `COMMAND_AMIITOOL`, this command, and 0x00 as one BLE package, then an acknowledgement will be sent back consisting of the command used.
            /// After the acknowledgement, exactly 160 bytes should be sent.
            /// This command will wait until all data has been received before more commands can be executed.
            /// - Parameter key: An optional 160 byte key combination.
            /// - Returns: 0x00 if the key is present, or 0x01 if it is not. If setting the key, the key will be stored if the data is valid. 0x01 will be returned if the data is invalid, otherwise 0x00 will be returned.
            case key = 0x00
            
            /// When the key is set, this command will generate a blank amiibo tag with a random UID.
            /// - Parameters:
            ///   - slot: A byte indicating the slot number.
            ///   - figureId: The 8 bytes of the figure ID.
            /// - Returns: If the key is not set, `BAD_COMMAND` will be returned. Otherwise, the command, slot, and the nine byte UID of the generated tag will be returned.
            case generateTag = 0x01
            
            /// When the key is set, this command will randomize the UID of the tag in the specified slot.
            /// An optional UID can be sent to set the UID to a specific value.
            /// - Parameters:
            ///   - slot: A byte indicating the slot number.
            ///   - uid: An optional 9 byte UID.
            /// - Returns: The command, sub-command, slot, and the nine byte UID of the generated tag.
            case changeUid = 0x02
        }
    }
}

class PuckPeripheral: NSObject {
    let peripheral: Peripheral
    
    /// A boolean property indicating if amiitool functionality is enabled.
    private(set) var amiitoolEnabled = false
    private(set) var amiitoolKeysLoaded = false
    
    fileprivate static let serviceUuid = "6e400001-b5a3-f393-e0a9-e50e24dcca9e"
    fileprivate static let commandUuid = "6e400002-b5a3-f393-e0a9-e50e24dcca9e"
    fileprivate static let responseUuid = "6e400003-b5a3-f393-e0a9-e50e24dcca9e"

    fileprivate static var observersRegistered = false
    fileprivate var disconnecting = false
    fileprivate var _name: String? = nil
    fileprivate var packetSize = 20
    @Published fileprivate var lastReceivedData: Data? = nil
    fileprivate var cancellables = Set<AnyCancellable>()
    fileprivate var isSetup = false
    
    static var pucks: [PuckPeripheral] = []
    static var scanning = false
    
    var name: String {
        return self._name ?? peripheral.name ?? "Puck"
    }
    
    init(peripheral: Peripheral){
        self.peripheral = peripheral
        super.init()
        
        cancellables.insert($lastReceivedData
            .sink { data in
                if let data = data {
                    if data.count > self.packetSize {
                        self.packetSize = data.count
                        print("Increased packet size to \(self.packetSize)")
                    }
                    
                    print("Received")
                    self.printHexData(data)
                }
            })
    }
    
    fileprivate func printHexData(_ data: Data) {
        let hexString = data.map { String(format: "%02hhx ", $0) }.joined()
        let length = hexString.count

        for i in stride(from: 0, to: length, by: 30) {
            let end = min(i + 30, length)
            let range = hexString.index(hexString.startIndex, offsetBy: i)..<hexString.index(hexString.startIndex, offsetBy: end)
            print("\(hexString[range]) ")
        }
    }
    
    fileprivate func connect() async throws {
        return try await withCheckedThrowingContinuation { continuation in
            peripheral.connect(withTimeout: 5.0) { result in
                switch result {
                case .success(()):
                    continuation.resume()
                    break
                case .failure(let error):
                    continuation.resume(throwing: error)
                    break
                }
            }
        }
    }
    
    @objc fileprivate func receiveNotification(_ notification: Notification) {
        let charac = notification.userInfo!["characteristic"] as! CBCharacteristic
        if let error = notification.userInfo?["error"] as? SBError {
            Task {
                try? await self.disconnectAsync()
            }
            
            return
        }
        
        if let data = charac.value {
            self.lastReceivedData = data
        }
    }
        
    fileprivate func setNotification(toEnabled enabled: Bool) async throws -> Bool {
        return try await withCheckedThrowingContinuation { continuation in
            if enabled {
                NotificationCenter.default.addObserver(self, selector: #selector(receiveNotification), name: Peripheral.PeripheralCharacteristicValueUpdate, object: peripheral)
            } else {
                NotificationCenter.default.removeObserver(self, name: Peripheral.PeripheralCharacteristicValueUpdate, object: peripheral)
            }
            
            self.peripheral.setNotifyValue(toEnabled: enabled, forCharacWithUUID: PuckPeripheral.responseUuid, ofServiceWithUUID: PuckPeripheral.serviceUuid) { (result) in
                switch result {
                    case .success(let isNotifying):
                        continuation.resume(returning: isNotifying)
                        break
                    case .failure(let error):
                        continuation.resume(throwing: error)
                        break
                }
            }
        }
    }
    
    fileprivate func doSetup() async throws {
        if !isSetup {
            try await connect()
            if (try await setNotification(toEnabled: true)) == false {
                throw AmiiTagError(description: "Error starting BLE notifications")
            }
            try await fastMode()
            
            isSetup = true
            
            amiitoolEnabled = try await checkAmiitool()
            
            if amiitoolEnabled {
                amiitoolKeysLoaded = try await checkAmiitoolKeys()
                
                if (!amiitoolKeysLoaded && KeyFiles.hasKeys) {
                    amiitoolKeysLoaded = try await setAmiitoolKeys(keys: Data(KeyFiles.dataKey!.data + KeyFiles.staticKey!.data))
                }
            }
        }
    }
    
    // MARK: Bluetooth helper functions
    /// Send bytes to the puck
    /// - Parameter bytes: The bytes to send
    /// - Throws: Any errors that occur
    fileprivate func sendBytesAsync(bytes: Data, characteristicUuid: String = PuckPeripheral.commandUuid, writeType: CBCharacteristicWriteType = .withResponse) async throws {
        return try await withCheckedThrowingContinuation { continuation in
            self.peripheral.writeValue(ofCharacWithUUID: characteristicUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid, value: Data(bytes), type: writeType) { (result) in
                switch result {
                case .failure(let error):
                    continuation.resume(throwing: error)
                    break
                case .success(()):
                    print("Sent")
                    self.printHexData(bytes)
                    continuation.resume()
                }
            }
        }
    }
    
    /// Read bytes from the puck
    /// - Throws: Any errors that occur
    fileprivate func readBytesAsync() async throws -> Data {
        return try await withCheckedThrowingContinuation { continuation in
            self.peripheral.readValue(ofCharacWithUUID: PuckPeripheral.responseUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid) { (result) in
                switch result {
                case .failure(let error):
                    continuation.resume(throwing: error)
                    break
                case .success(let data):
                    continuation.resume(returning: data)
                    break
                }
            }
        }
    }
    
    /// Send bytes and wait for a response.
    /// - Parameter bytes: The bytes to send. They will automatically be chunked according to the packet size.
    /// - Parameter count: The number of bytes to read in response. If 0, the entire response in the first packet will be read.
    /// - Parameter timeout: The timeout to wait for a response.
    /// - Returns: The response data
    /// - Throws: Any errors that occur
    func sendAndReadNextAsync(bytes: Data, count: Int = 0, timeout: TimeInterval = 5.0) async throws -> Data {
        return try await sendAndReadNextAsync(bytes: bytes, count: count, timeout: timeout) { _ in }
    }
    
    /// Send bytes and wait for a response.
    /// - Parameter bytes: The bytes to send. They will automatically be chunked according to the packet size.
    /// - Parameter count: The number of bytes to read in response. If 0, the entire response in the first packet will be read.
    /// - Parameter timeout: The timeout to wait for a response.
    /// - Returns: The response data
    /// - Throws: Any errors that occur
    func sendAndReadNextAsync(bytes: Data, count: Int = 0, timeout: TimeInterval = 5.0, progress: @escaping ((sendProgress: BleProgress?, receiveProgress: BleProgress?)) -> Void) async throws -> Data {
        return try await withCheckedThrowingContinuation { continuation in
            var errorTimer: Timer?
            var storage = Data(count: count)
            var currentOffset = 0
            var cancellable: AnyCancellable? = nil
            
            
            
            func clearCancellable() {
                cancellable?.cancel()
                cancellable = nil
            }
            
            func resetTimeout() {
                if timeout > 0 {
                    errorTimer?.invalidate()
                    errorTimer = Timer.scheduledTimer(withTimeInterval: timeout, repeats: false) { _ in
                        clearCancellable()
                        continuation.resume(throwing: NSError(domain: "Read timeout.", code: 0, userInfo: nil))
                    }
                }
            }

            func finishName(_ value: Data) {
                let response = [UInt8](value)
                
                resetTimeout()

                if count > 0 {
                    for byte in value {
                        storage[currentOffset] = byte
                        currentOffset += 1
                        
                        progress((sendProgress: nil, receiveProgress: BleProgress(current: currentOffset, total: count)))

                        if currentOffset >= count {
                            errorTimer?.invalidate()
                            clearCancellable()
                            continuation.resume(returning: storage)
                        }
                    }
                } else {
                    errorTimer?.invalidate()
                    clearCancellable()
                    continuation.resume(returning: value)
                }
            }
            
            cancellable = self.$lastReceivedData
                .dropFirst()
                .sink(receiveValue: { data in
                    if let data = data {
                        finishName(data)
                    }
                })
            
            Task {
                for i in stride(from: 0, to: bytes.count, by: self.packetSize) {
                    resetTimeout()
                    let data = bytes.subdata(in: i..<min(i + self.packetSize, bytes.count))
                    
                    progress((sendProgress: BleProgress(current: i, total: bytes.count), receiveProgress: nil))
                    try await self.sendBytesAsync(bytes: data)
                    progress((sendProgress: BleProgress(current: i + data.count, total: bytes.count), receiveProgress: nil))
                }
            }
        }
    }
    
    // MARK: Puck functions
    fileprivate func fastMode() async throws {
        return try await withCheckedThrowingContinuation { continuation in
            var accumulatedData = Data()
            var cancellable: AnyCancellable?
            
            var errorTimer = Timer.scheduledTimer(withTimeInterval: 8.0, repeats: false) { _ in
                
                continuation.resume(throwing: AmiiTagError(description: "Fast mode string not found"))
            }
            
            cancellable = $lastReceivedData.dropFirst().sink { value in
                if let value = value {
                    accumulatedData.append(value)
                    
                    self.printHexData(accumulatedData)
                    
                    if accumulatedData.contains("DTM_PUCK_FAST".data(using: .ascii)!) {
                        print("Fast mode enabled")
                        
                        errorTimer.invalidate()
                        
                        cancellable?.cancel()
                        cancellable = nil
                        
                        continuation.resume()
                    }
                }
            }
            
            Task {
                do {
                    let _ = try await sendAndReadNextAsync(bytes: "fastMode()\n".data(using: .ascii)!)
                } catch {
                    cancellable?.cancel()
                    cancellable = nil
                    continuation.resume(throwing: error)
                }
            }
        }
    }
    
    /// Get the total number of slots and the current slot
    /// - Returns: A tuple containing the current slot and total slots
    /// - Throws: Any errors that occur
    func getSlotSummaryAsync() async throws -> (current: UInt8, total: UInt8) {
        try await doSetup()
        
        let response = try await sendAndReadNextAsync(bytes: Data([Commands.slotInformation.rawValue]))
        
        if response[0] == 0x01 {
            return (current: response[1], total: response[2])
        } else {
            throw PuckError(description: "Slot summary response invalid")
        }
    }
    
    /// Get the total number of slots and the current slot
    /// - Parameter completionHandler: A closure to call with the result
    func getSlotSummary(completionHandler: @escaping (Result<(current: UInt8, total: UInt8), Error>) -> Void){
        Task {
            do {
                let summary = try await getSlotSummaryAsync()
                completionHandler(.success(summary))
            } catch {
                completionHandler(.failure(error))
            }
        }
    }
    
    func getSlotInformationAsync(slot: UInt8 = 255, count: UInt8 = 1) async throws -> [SlotInfo] {
        return try await getSlotInformationAsync(slot: slot, count: count) { _ in }
    }
    
    /// Get information for a slot
    /// - Parameter slot: The slot to get information for
    /// - Returns: A SlotInfo struct
    /// - Throws: Any errors that occur
    func getSlotInformationAsync(slot: UInt8 = 255, count: UInt8 = 1, progress: @escaping (BleProgress) -> Void) async throws -> [SlotInfo] {
        try await doSetup()
        
        var command = Data([Commands.slotInformation.rawValue, slot, count])
        var infoCollection = [SlotInfo]()
        
        print("Getting slot information: \(slot) for \(count) slot\(count > 1 ? "s" : "")")
        let receivedData = try await sendAndReadNextAsync(bytes: command, count: 82 * Int(count)) { (sendProgress, receiveProgress) in
            if let status = receiveProgress {
                progress(BleProgress(current: status.current, total: status.total))
            }
        }
        
        // Loop through the data chunks 82 bytes at a time
        for i in stride(from: 0, to: receivedData.count, by: 82) {
            let data = Data(receivedData[i..<min(i + 82, receivedData.count)])
            let chunkSlot = UInt8(floor(Double(i) / 82.0))
            
            if data.count != 82 || !(data[0] == 0x01 && data[1] == chunkSlot) {
                throw AmiiTagError(description: "Failed to read slot \(chunkSlot)")
            }
            
            var tagData = Data(count: 572)
            let response = Data(data[2..<data.count])
            tagData[0..<8] = response[0..<8]
            tagData[16..<28] = response[8..<20]
            tagData[32..<52] = response[20..<40]
            tagData[84..<92] = response[40..<48]
            tagData[96..<128] = response[48..<80]
            
            let tag = TagDump(data: Data(tagData))!
            infoCollection.append(SlotInfo(slot: chunkSlot, name: tag.displayName, idHex: "0x\(tag.headHex)\(tag.tailHex)", dump: tag))
        }
        
        return infoCollection
    }
    
    /// Get information for a slot
    /// - Parameter slot: The slot to get information for
    func getSlotInformation(slot: UInt8 = 255, completionHandler: @escaping (Result<SlotInfo, Error>) -> Void){
        Task {
            do {
                let info = try await getSlotInformationAsync(slot: slot)
                completionHandler(.success(info[0]))
            } catch {
                completionHandler(.failure(error))
            }
        }
    }
    
    /// Get information for all slots
    /// - Returns: An array of SlotInfo structs
    /// - Throws: Any errors that occur
    func getAllSlotInformationAsync(progress: @escaping (SlotStatus) -> Void) async throws -> [SlotInfo] {
        try await doSetup()
        
        var data = [SlotInfo]()
        let summary = try await getSlotSummaryAsync()
        
        return try await getSlotInformationAsync(slot: 0, count: summary.total) { (status) in
            progress(SlotStatus(current: UInt8(round(Double(status.current) / 82.0)), total: UInt8(round(Double(status.total) / 82.0))))
        }
    }
    
    /// Get information for all slots
    /// - Parameter completionHandler: A closure to call with the result
    func getAllSlotInformation(completionHandler: @escaping (StatusResult<[SlotInfo], SlotStatus, Error>) -> Void){
        Task {
            do {
                let info = try await getAllSlotInformationAsync { (status) in
                    completionHandler(.status(status))
                }
                completionHandler(.success(info))
            } catch {
                completionHandler(.failure(error))
            }
        }
    }
    
    /// Read a tag from the puck
    /// - Parameter slot: The slot to read from
    /// - Returns: The tag data
    /// - Throws: Any errors that occur
    func readTagAsync(slot: UInt8, progress: @escaping (TagStatus) -> Void) async throws -> Data {
        try await doSetup()
        
        progress(TagStatus(slot: slot, start: 0, count: 572, total: 572))
        
        let response = try await sendAndReadNextAsync(bytes: Data([Commands.read.rawValue, slot, 0, 143]), count: 576) { (sendProgress, receiveProgress) in
            if let status = receiveProgress {
                progress(TagStatus(slot: slot, start: status.current, count: status.total - status.current, total: status.total))
            }
        }
        let tagData = response[4...]
        
        progress(TagStatus(slot: slot, start: 572, count: 0, total: 572))
        
        return Data(tagData)
    }
    
    /// Read a tag from the puck
    /// - Parameter slot: The slot to read from
    /// - Parameter completionHandler: A closure to call with the result
    func readTag(slot: UInt8 = 255, completionHandler: @escaping (StatusResult<Data, TagStatus, Error>) -> Void){
        Task {
            do {
                let result = try await readTagAsync(slot: slot) { (status) in
                    completionHandler(.status(status))
                }
                
                completionHandler(.success(result))
            } catch {
                completionHandler(.failure(error))
            }
        }
    }
    
    /// Read all tags from the puck
    /// - Returns: An array of tag data
    /// - Throws: Any errors that occur
    func readAllTagsAsync(progress: @escaping (TagStatus) -> Void) async throws -> [Data] {
        try await doSetup()
        
        var tags = [Data]()
        let summary = try await getSlotSummaryAsync()
        
        for slot in 0..<summary.total {
            let tag = try await readTagAsync(slot: slot, progress: progress)
            tags.append(tag)
        }
        
        return tags
    }
    
    /// Read all tags from the puck
    /// - Parameter completionHandler: A closure to call with the result
    func readAllTags(completionHandler: @escaping (StatusResult<[Data], TagStatus, Error>) -> Void){
        Task {
            do {
                let result = try await self.readAllTagsAsync { (status) in
                    completionHandler(.status(status))
                }
                
                completionHandler(.success(result))
            } catch {
                completionHandler(.failure(error))
            }
        }
    }
    
    /// Write a tag to the puck
    /// - Parameters:
    ///  - slot: The slot to write to
    ///  - data: The tag data to write
    ///  - progress: A closure to call with progress updates
    /// - Returns: The tag data
    /// - Throws: Any errors that occur
    func writeTagAsync(toSlot chosenSlot: UInt8? = nil, using tag: Data, progress: @escaping (TagStatus) -> Void) async throws {
        try await doSetup()
        
        var data = Data(count: 572)
        var slot: UInt8! = nil
        data[0..<tag.count] = tag[0..<tag.count]
        
        if (chosenSlot == nil) {
            let summary = try await getSlotSummaryAsync()
            slot = summary.current
        } else {
            slot = chosenSlot
        }
        
        print("Writing to \(self.name) in slot \(slot!) for \(data.count) bytes")
        
        let _ = try await sendAndReadNextAsync(bytes: Data([Commands.fullWrite.rawValue, slot]))
        let writeResult = try await sendAndReadNextAsync(bytes: data) { (sendProgress, readProgress) in
            if let status = sendProgress {
                progress(TagStatus(slot: slot, start: status.current, count: data.count - status.current, total: status.total))
            }
        }
        
        print("Saving \(self.name) in slot \(slot!)")
        _ = try await sendAndReadNextAsync(bytes: Data([Commands.save.rawValue, slot]))
    }
    
    /// Write a tag to the puck
    /// - Parameters:
    ///  - slot: The slot to write to
    ///  - data: The tag data to write
    ///  - completionHandler: A closure to call with the result
    func writeTag(toSlot slot: UInt8? = nil, using tag: Data, completionHandler: @escaping (StatusResult<Void, TagStatus, Error>) -> Void){
        Task {
            do {
                try await writeTagAsync(toSlot: slot, using: tag) { (status) in
                    completionHandler(.status(status))
                }
                
                completionHandler(.success(()))
            } catch {
                completionHandler(.failure(error))
            }
        }
    }
    
    /// Checks if the amiitool functionality is available on this puck.
    /// - Returns: A boolean indicating if the amiitool functionality is available
    func checkAmiitool() async throws -> Bool {
        if (amiitoolEnabled) {
            return true
        }
        
        let result = try await sendAndReadNextAsync(bytes: Data([Commands.amiitool.rawValue]))
        
        if result.count > 1 {
            return true
        }
        
        return false
    }
    
    /// Checks if the amiitool functionality is available on this puck.
    /// - Parameter completion: A closure to call with the result
    /// - Returns: A boolean indicating if the amiitool functionality
    func checkAmiitool(completion: @escaping (Result<Bool, Error>) -> Void){
        Task {
            do {
                let result = try await checkAmiitool()
                
                completion(.success(result))
            } catch {
                completion(.failure(error))
            }
        }
    }
    
    /// Checks if the amiitool keys are set on this puck.
    /// - Returns: A boolean indicating if the amiitool keys are set
    func checkAmiitoolKeys() async throws -> Bool {
        if (amiitoolKeysLoaded) {
            return true
        }
        
        let result = try await sendAndReadNextAsync(bytes: Data([Commands.amiitool.rawValue, Commands.AmiitoolSubCommand.key.rawValue]))
        
        if (result.count == 1 && result[0] == 0x00) {
            return true
        }
        
        return false
    }
    
    /// Checks if the amiitool keys are set on this puck.
    /// - Parameter completion: A closure to call with the result
    /// - Returns: A boolean indicating if the amiitool keys are set
    func checkAmiitoolKeys(completion: @escaping (Result<Bool, Error>) -> Void){
        Task {
            do {
                let result = try await checkAmiitoolKeys()
                completion(.success(result))
            } catch {
                completion(.failure(error))
            }
        }
    }

    
    /// Set the amiitool keys on this puck.
    /// - Parameter keys: The amiitool keys to set
    /// - Returns: A boolean indicating if the keys were set
    func setAmiitoolKeys(keys: Data) async throws -> Bool {
        guard keys.count == 160 else {
            throw AmiiTagError(description: "Invalid key length")
        }
        
        let uploadCommand = Data([Commands.amiitool.rawValue, Commands.AmiitoolSubCommand.key.rawValue, 0x00])
        let result = try await sendAndReadNextAsync(bytes: uploadCommand)
        
        // Compare the result to the upload command
        guard result == uploadCommand else {
            throw PuckError(description: "Received invalid response when attempting to set amiitool keys")
        }
        
        let uploadResult = try await sendAndReadNextAsync(bytes: keys)
        
        if uploadResult.count == 1 && uploadResult[0] == 0x00 {
            amiitoolKeysLoaded = true
            
            return true
        }
        
        return false
    }
    
    /// Set the amiitool keys on this puck.
    /// - Parameter keys: The amiitool keys to set
    /// - Parameter completion: A closure to call with the result
    /// - Returns: A boolean indicating if the keys were set
    func setAmiitoolKeys(keys: Data, completion: @escaping (Result<Bool, Error>) -> Void){
        Task {
            do {
                let result = try await setAmiitoolKeys(keys: keys)
                completion(.success(result))
            } catch {
                completion(.failure(error))
            }
        }
    }
    
    /// Randomize the UID of the tag in the specified slot
    /// - Parameter slot: The slot to randomize the UID for
    /// - Returns: The new UID
    func randomizeUid(slot: UInt8) async throws -> Data {
        guard amiitoolEnabled else {
            throw AmiiTagError(description: "Amiitool functionality is not enabled")
        }
        
        guard amiitoolKeysLoaded else {
            throw AmiiTagError(description: "Amiitool keys are not loaded")
        }
        
        let result = try await sendAndReadNextAsync(bytes: Data([Commands.amiitool.rawValue, Commands.AmiitoolSubCommand.changeUid.rawValue, slot]))
        
        guard result.count == 12 && result[0] == Commands.amiitool.rawValue && result[1] == Commands.AmiitoolSubCommand.changeUid.rawValue && result[2] == slot else {
            throw PuckError(description: "Invalid response when randomizing UID")
        }
        
        // Return the UID
        let uid = Data(result[3...])
        
        return uid
    }
    
    /// Randomize the UID of the tag in the specified slot
    func randomizeUid(slot: UInt8, completionHandler: @escaping (Result<Data, Error>) -> Void){
        Task {
            do {
                let result = try await randomizeUid(slot: slot)
                completionHandler(.success(result))
            } catch {
                completionHandler(.failure(error))
            }
        }
    }
    
    /// Disconnect from the puck
    /// - Throws: Any errors that occur
    func disconnectAsync() async throws {
        if self.disconnecting || self.peripheral.state == .disconnected || self.peripheral.state == .disconnecting {
            return
        }
        
        self.disconnecting = true
        print("Disconnecting from \(name)")
        
        let _ = try? await setNotification(toEnabled: false)
        
        return try await withCheckedThrowingContinuation { continuation in
            Task {
                // Read bytes to ensure that iOS flushes any cached data before disconnecting
                let _ = try? await readBytesAsync()
                
                // Disconnect from the puck
                self.peripheral.disconnect { result in
                    // Reset the disconnecting flag
                    self.disconnecting = false
                    
                    // Resume the continuation with the result of the disconnect
                    switch result {
                    case .success(()):
                        continuation.resume()
                        break
                    case .failure(let error):
                        continuation.resume(throwing: error)
                        break
                    }
                }
            }
        }
    }
    
    /// Disconnect from the puck
    /// - Parameter completionHandler: A closure to call when the disconnect is complete
    func disconnect(completionHandler: @escaping (Result<Void, Error>) -> Void) {
        Task {
            do {
                try await disconnectAsync()
                completionHandler(.success(()))
            } catch {
                completionHandler(.failure(error))
            }
        }
    }
    
    /// Change the name of the puck
    /// - Parameter name: The new name for the puck
    /// - Throws: Any errors that occur
    func changeNameAsync(name: String) async throws {
        try await doSetup()
        
        let command = name.data(using: .utf8)!
        
        if command.count > 20 {
            throw AmiiTagError(description: "Name is too long (\(command.count) bytes)")
        }
        
        self._name = name
        
        let _ = try await sendAndReadNextAsync(bytes: Data([Commands.setBluetoothName.rawValue]) + command)
        try? await disconnectAsync()
    }
    
    /// Change the name of the puck
    /// - Parameter name: The new name for the puck
    /// - Parameter completionHandler: A closure to call when the name change is complete
    func changeName(name: String, completionHandler: @escaping (Result<Void, Error>) -> Void){
        Task {
            do {
                try await changeNameAsync(name: name)
                completionHandler(.success(()))
            } catch {
                completionHandler(.failure(error))
            }
        }
    }
    
    /// Enable UART mode on the puck
    /// - Throws: Any errors that occur
    func enableUartAsync() async throws {
        try await sendBytesAsync(bytes: Data([Commands.enableBleUart.rawValue]))
        try? await disconnectAsync()
    }
    
    /// Enable UART mode on the puck
    /// - Parameter completionHandler: A closure to call when the UART mode is enabled
    func enableUart(completionHandler: @escaping (Result<Void, Error>) -> Void){
        Task {
            do {
                try await enableUartAsync()
                completionHandler(.success(()))
            } catch {
                completionHandler(.failure(error))
            }
        }
    }
    
    /// Change the current slot
    /// - Parameter slot: The slot to change to, or nil to reload the current slot
    /// - Throws: Any errors that occur
    func changeSlotAsync(slot: UInt8? = nil) async throws {
        var command: Data!
        if let slot = slot {
            command = Data([Commands.restartNfc.rawValue, slot])
        } else {
            // If no slot is provided, reload the current slot
            command = Data([Commands.restartNfc.rawValue])
        }
        
        try await sendBytesAsync(bytes: command)
    }
    
    /// Change the current slot
    /// - Parameter slot: The slot to change to, or nil to reload the current slot'
    func changeSlot(slot: UInt8? = nil, completionHandler: @escaping (Result<Void, Error>) -> Void){
        Task {
            do {
                try await changeSlotAsync(slot: slot)
                completionHandler(.success(()))
            } catch {
                completionHandler(.failure(error))
            }
        }
    }
    
    func clearSlot(slot: UInt8? = nil) async throws -> ClearResult {
        var summarySlot: UInt8? = nil
        
        if slot == nil {
            let summary = try await getSlotSummaryAsync()
            summarySlot = summary.current
        }
        
        var result = try await sendAndReadNextAsync(bytes: Data([Commands.clearSlot.rawValue, slot ?? summarySlot!]), count: 11)
        
        guard result[0] == Commands.clearSlot.rawValue else {
            throw PuckError(description: "Invalid clear response")
        }
        
        return ClearResult(slot: result[1], uid: result[2...])
    }
    
    func clearSlot(slot: UInt8? = nil, completionHandler: @escaping (Result<ClearResult, Error>) -> Void){
        Task {
            do {
                let result = try await clearSlot(slot: slot)
                completionHandler(.success(result))
            } catch {
                completionHandler(.failure(error))
            }
        }
    }
    
    static func startScanning() {
        scanning = true
        
        SwiftyBluetooth.scanForPeripherals(withServiceUUIDs: [serviceUuid], timeoutAfter: 15) { scanResult in
            switch scanResult {
            case .scanStarted:
                // The scan started meaning CBCentralManager scanForPeripherals(...) was called
                pucks.removeAll()
                NotificationCenter.default.post(name: .PuckPeripheralPucksChanged, object: nil)
                break
            case .scanResult(let peripheral, _, _):
                // A peripheral was found, your closure may be called multiple time with a .ScanResult enum case.
                // You can save that peripheral for future use, or call some of its functions directly in this closure.
                pucks.append(PuckPeripheral(peripheral: peripheral))
                NotificationCenter.default.post(name: .PuckPeripheralPucksChanged, object: nil)
                break
            case .scanStopped(peripherals: _, error: let error):
                // The scan stopped, an error is passed if the scan stopped unexpectedly
                if error == nil && scanning {
                    startScanning()
                } else {
                    scanning = false
                    pucks.removeAll()
                    NotificationCenter.default.post(name: .PuckPeripheralPucksChanged, object: nil)
                }
                break
            }
        }
    }
    
    static func stopScanning() {
        scanning = false
        if SwiftyBluetooth.isScanning {
            SwiftyBluetooth.stopScan()
        }
    }
    
    static func registerObservers(){
        if !PuckPeripheral.observersRegistered {
            PuckPeripheral.observersRegistered = true
            
            NotificationCenter.default.addObserver(forName: Central.CentralStateChange, object: Central.sharedInstance, queue: nil) { (notification) in
                if let state = notification.userInfo?["state"] as? CBManagerState {
                    switch state {
                    case .poweredOn:
                        startScanning()
                        break
                        
                    case .poweredOff:
                        PuckPeripheral.pucks.removeAll()
                        NotificationCenter.default.post(name: .PuckPeripheralPucksChanged, object: nil)
                        break
                    default: break
                    }
                }
            }
        }
    }
    
    static func getPuckChooser(puckChosen: @escaping(PuckPeripheral) -> Void) -> UIAlertController? {
        if PuckPeripheral.pucks.count > 0 {
            let alertController = UIAlertController(title: "Select Puck", message: nil, preferredStyle: .actionSheet)
            
            for puck in PuckPeripheral.pucks.sorted(by: { (a, b) -> Bool in
                return a.name > b.name
            }) {
                alertController.addAction(UIAlertAction(title: puck.name, style: .default, handler: { (action) in
                    puckChosen(puck)
                }))
            }
            
            alertController.addAction(UIAlertAction(title: "Cancel", style: .cancel){ action -> Void in })
            return alertController
        }
        
        let alertController = UIAlertController(title: "No Pucks Available", message: "There are no pucks currently available.", preferredStyle: .alert)
        
        alertController.addAction(UIAlertAction(title: "OK", style: .default))
        
        return alertController
    }
}
