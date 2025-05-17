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
#if false
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
}

class PuckPeripheral: NSObject {
    let peripheral: Peripheral
    
    fileprivate static let serviceUuid = "78290001-d52e-473f-a9f4-f03da7c67dd1"
    fileprivate static let commandUuid = "78290002-d52e-473f-a9f4-f03da7c67dd1"
    fileprivate static let responseUuid = "78290003-d52e-473f-a9f4-f03da7c67dd1"
    fileprivate static let nameUuid = "78290004-d52e-473f-a9f4-f03da7c67dd1"
    fileprivate static var observersRegistered = false
    fileprivate var disconnecting = false
    fileprivate var _name: String? = nil
    fileprivate var packetSize = 20
    @Published fileprivate var lastReceivedData: Data? = nil
    fileprivate var cancellables = Set<AnyCancellable>()
    
    static var pucks: [PuckPeripheral] = []
    static var scanning = false
    
    var name: String {
        return self._name ?? peripheral.name ?? "Puck"
    }
    
    init(peripheral: Peripheral){
        self.peripheral = peripheral
    }
    
    // MARK: Bluetooth helper functions
    /// Send bytes to the puck
    /// - Parameter bytes: The bytes to send
    /// - Throws: Any errors that occur
    fileprivate func sendBytesAsync(bytes: Data, characteristicUuid: String = PuckPeripheral.commandUuid) async throws {
        return try await withCheckedThrowingContinuation { continuation in
            self.peripheral.writeValue(ofCharacWithUUID: characteristicUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid, value: Data(bytes), type: .withResponse) { (result) in
                switch result {
                case .failure(let error):
                    continuation.resume(throwing: error)
                    break
                case .success(()):
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
    
    /// Send bytes to the puck and read the next response
    /// - Parameter bytes: The bytes to send
    /// - Throws: Any errors that occur
    fileprivate func sendAndReadNextAsync(bytes: Data) async throws -> Data {
        try await sendBytesAsync(bytes: bytes)
        return try await readBytesAsync()
    }
    
    // MARK: Puck functions
    
    /// Get the total number of slots and the current slot
    /// - Returns: A tuple containing the current slot and total slots
    /// - Throws: Any errors that occur
    func getSlotSummaryAsync() async throws -> (current: UInt8, total: UInt8) {
        let response = try await sendAndReadNextAsync(bytes: Data([0x01]))
        
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
    
    /// Get information for a slot
    /// - Parameter slot: The slot to get information for
    /// - Returns: A SlotInfo struct
    /// - Throws: Any errors that occur
    func getSlotInformationAsync(slot: UInt8 = 255) async throws -> SlotInfo {
        let command = Data([0x01, slot])
        
        print("Getting slot information for \(slot)")
        try await sendBytesAsync(bytes: command)
        
        for attempts in 1...3 {
            let data = try await readBytesAsync()
            
            if data.count != 82 || !(data[0] == 0x01 && data[1] == slot) {
                if attempts <= 3 {
                    print("Retry read of tag info for slot \(slot), attempt \(attempts)")
                    continue
                }
            }
            
            var tagData = Data(count: 572)
            let response = Data(data[2..<data.count])
            tagData[0..<8] = response[0..<8]
            tagData[16..<28] = response[8..<20]
            tagData[32..<52] = response[20..<40]
            tagData[84..<92] = response[40..<48]
            tagData[96..<128] = response[48..<80]
            
            let tag = TagDump(data: Data(tagData))!
            return SlotInfo(slot: slot, name: tag.displayName, idHex: "0x\(tag.headHex)\(tag.tailHex)", dump: tag)
        }
        
        throw AmiiTagError(description: "Failed to read slot \(slot) after 3 attempts")
    }
    
    /// Get information for a slot
    /// - Parameter slot: The slot to get information for
    func getSlotInformation(slot: UInt8 = 255, completionHandler: @escaping (Result<SlotInfo, Error>) -> Void){
        Task {
            do {
                let info = try await getSlotInformationAsync(slot: slot)
                completionHandler(.success(info))
            } catch {
                completionHandler(.failure(error))
            }
        }
    }
    
    /// Get information for all slots
    /// - Returns: An array of SlotInfo structs
    /// - Throws: Any errors that occur
    func getAllSlotInformationAsync(progress: @escaping (SlotStatus) -> Void) async throws -> [SlotInfo] {
        var data = [SlotInfo]()
        let summary = try await getSlotSummaryAsync()
        
        for slot in 0..<summary.total {
            progress(SlotStatus(current: slot, total: summary.total))
            let info = try await getSlotInformationAsync(slot: slot)
            data.append(info)
            progress(SlotStatus(current: slot + 1, total: summary.total))
        }
        
        return data
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
        let maxPages = 63
        var tagData = Data(count: 0)
        
        progress(TagStatus(slot: slot, start: 0, count: maxPages, total: 572))
        
        for startPage in stride(from: 0, through: 143, by: maxPages) {
            let count = min(143 - startPage, maxPages)
            let command = Data([0x02, slot, UInt8(startPage), UInt8(count)])
            
            progress(TagStatus(slot: slot, start: startPage * 4, count: count * 4, total: 572))
            
            print("Reading from \(peripheral.name ?? "puck") in slot \(slot) at page \(startPage) for \(count) pages")
            
            let result = try await sendAndReadNextAsync(bytes: command)
            
            tagData += result[4...]
            
        }
        
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
    func writeTagAsync(toSlot slot: UInt8, using tag: Data, progress: @escaping (TagStatus) -> Void) async throws {
        
        var data = Data(count: 572)
        data[0..<tag.count] = tag[0..<tag.count]
        progress(TagStatus(slot: slot, start: 0, count: data.count, total: 572))
        
        for startPage in stride(from: 0, through: 143, by: 4) {
            let dataToWrite = Data(data[(startPage * 4)..<min((((startPage) + 4) * 4), 572)])
            let command = Data([0x03, slot, UInt8(startPage)] + dataToWrite)
            
            progress(TagStatus(slot: slot, start: Int(startPage) * 4, count: dataToWrite.count, total: 572))
            
            print("Writing to \(self.name) in slot \(slot) at page \(startPage) for \(dataToWrite.count) bytes")
            
            try await sendBytesAsync(bytes: command)
        }
        
        progress(TagStatus(slot: slot, start: 572, count: 0, total: 572))
    }
    
    /// Write a tag to the puck
    /// - Parameters:
    ///  - slot: The slot to write to
    ///  - data: The tag data to write
    ///  - completionHandler: A closure to call with the result
    func writeTag(toSlot slot: UInt8 = 255, using tag: Data, completionHandler: @escaping (StatusResult<Void, TagStatus, Error>) -> Void){
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
    
    /// Disconnect from the puck
    /// - Throws: Any errors that occur
    func disconnectAsync() async throws {
        if self.disconnecting || self.peripheral.state == .disconnected || self.peripheral.state == .disconnecting {
            return
        }
        
        self.disconnecting = true
        print("Disconnecting from \(name)")
        
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
        let command = name.data(using: .utf8)!
        
        if command.count > 20 {
            throw AmiiTagError(description: "Name is too long (\(command.count) bytes)")
        }
        
        self._name = name
        
        try await sendBytesAsync(bytes: command, characteristicUuid: PuckPeripheral.nameUuid)
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
        try await sendBytesAsync(bytes: Data([0xFE]))
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
            command = Data([0xFF, slot])
        } else {
            // If no slot is provided, reload the current slot
            command = Data([0xFF])
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
#endif
