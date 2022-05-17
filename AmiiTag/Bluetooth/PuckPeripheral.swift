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

class PuckPeripheral: NSObject {
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
    
    fileprivate static let serviceUuid = "78290001-d52e-473f-a9f4-f03da7c67dd1"
    fileprivate static let commandUuid = "78290002-d52e-473f-a9f4-f03da7c67dd1"
    fileprivate static let responseUuid = "78290003-d52e-473f-a9f4-f03da7c67dd1"
    fileprivate static let nameUuid = "78290004-d52e-473f-a9f4-f03da7c67dd1"
    fileprivate static var observersRegistered = false
    static var pucks: [PuckPeripheral] = []
    static var scanning = false
    
    let peripheral: Peripheral
    fileprivate var disconnecting = false
    fileprivate var _name: String? = nil
    var name: String {
        return self._name ?? peripheral.name ?? "Puck"
    }
    
    init(peripheral: Peripheral){
        self.peripheral = peripheral
    }
    
    func disconnect(completionHandler: @escaping (Result<Void, Error>) -> Void) {
        if self.disconnecting || self.peripheral.state == .disconnected || self.peripheral.state == .disconnecting {
            completionHandler(.success(()))
            
            return
        }
        
        self.disconnecting = true
        print("Disconnecting from \(name)")
        peripheral.readValue(ofCharacWithUUID: PuckPeripheral.commandUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid) { (result) in
            self.peripheral.disconnect { (result) in
                self.disconnecting = false
                completionHandler(result)
            }
        }
    }
    
    func changeName(name: String, completionHandler: @escaping (Result<Void, Error>) -> Void){
        let command = name.data(using: .utf8)!
        
        if command.count > 20 {
            completionHandler(.failure(AmiiTagError(description: "Name is too long (\(command.count) bytes)")))
            return
        }
        
        self._name = name
        
        peripheral.writeValue(ofCharacWithUUID: PuckPeripheral.nameUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid, value: command, type: .withResponse) { (result) in
            self.disconnect { (result) in
                completionHandler(result)
            }
        }
    }
    
    func enableUart(completionHandler: @escaping (Result<Void, Error>) -> Void){
        let command = Data([0xFE])
        
        peripheral.writeValue(ofCharacWithUUID: PuckPeripheral.commandUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid, value: command, type: .withResponse) { (result) in
            self.disconnect(completionHandler: completionHandler)
        }
    }
    
    func getSlotSummary(completionHandler: @escaping (Result<(current: UInt8, total: UInt8), Error>) -> Void){
        let command = Data([0x01])
        peripheral.writeValue(ofCharacWithUUID: PuckPeripheral.commandUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid, value: command, type: .withResponse) { (result) in
            switch result {
            case .success(()):
                self.peripheral.readValue(ofCharacWithUUID: PuckPeripheral.responseUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid) { (result) in
                    switch result {
                    case .success(let response):
                        if response[0] == 0x01 {
                            completionHandler(.success((current: response[1], total: response[2])))
                        } else {
                            completionHandler(.failure(PuckError(description: "Slot summary response invalid")))
                        }
                        break
                    case .failure(let error):
                        completionHandler(.failure(error))
                        break
                    }
                }
                break
            case .failure(let error):
                completionHandler(.failure(error))
                break
            }
        }
    }
    
    func getAllSlotInformation(completionHandler: @escaping (StatusResult<[SlotInfo], SlotStatus, Error>) -> Void){
        getSlotSummary { (result) in
            switch result {
            case .success(let summary):
                self._getAllSlotInformation(current: 0, total: summary.total, data: nil, completionHandler: completionHandler)
                break
            case .failure(let error):
                completionHandler(.failure(error))
                break
            }
        }
    }
    
    fileprivate func _getAllSlotInformation(current: UInt8 = 0, total: UInt8 = 1, data: [SlotInfo]? = nil, completionHandler: @escaping (StatusResult<[SlotInfo], SlotStatus, Error>) -> Void){
        var data: [SlotInfo] = data ?? []
        if current < total {
            completionHandler(.status(SlotStatus(current: current, total: total)))
            getSlotInformation(slot: current) { (result) in
                switch result {
                case .success(let result):
                    data.append(result)
                    self._getAllSlotInformation(current: current + 1, total: total, data: data, completionHandler: completionHandler)
                    break
                case .failure(let error):
                    completionHandler(.failure(error))
                    break
                }
            }
        } else {
            completionHandler(.success(data))
        }
    }
    
    func handleSlotInformationRead(slot: UInt8, attempts: Int = 0, completionHandler: @escaping (Result<SlotInfo, Error>) -> Void) {
        self.peripheral.readValue(ofCharacWithUUID: PuckPeripheral.responseUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid) { (result) in
            switch result {
            case .success(let data):
                if data.count != 82 || !(data[0] == 0x01 && data[1] == slot) {
                    if attempts < 3 {
                        print("Retry read of tag info for slot \(slot), attempt \(attempts)")
                        self.handleSlotInformationRead(slot: slot, attempts: attempts + 1, completionHandler: completionHandler)
                    } else {
                        completionHandler(.failure(AmiiTagError(description: "Failed to read slot \(slot) after \(attempts) attempts")))
                    }
                    return
                }
                var tagData = Data(count: 572)
                let response = Data(data[2..<data.count])
                tagData[0..<8] = response[0..<8]
                tagData[16..<28] = response[8..<20]
                tagData[32..<52] = response[20..<40]
                tagData[84..<92] = response[40..<48]
                tagData[96..<128] = response[48..<80]
                
                let data = Data(tagData)
                let tag = TagDump(data: data)!
                completionHandler(.success(SlotInfo(slot: slot, name: tag.displayName, idHex: "0x\(tag.headHex)\(tag.tailHex)", dump: tag)))
                break
            case .failure(let error):
                completionHandler(.failure(error))
                break
            }
        }
    }
    
    func getSlotInformation(slot: UInt8 = 255, completionHandler: @escaping (Result<SlotInfo, Error>) -> Void){
        let command = Data([0x01, slot])
        print("Getting slot information for \(slot)")
        peripheral.writeValue(ofCharacWithUUID: PuckPeripheral.commandUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid, value: command, type: .withResponse) { (result) in
            switch (result) {
            case .success(()):
                self.handleSlotInformationRead(slot: slot, completionHandler: completionHandler)
                break
            case .failure(let error):
                completionHandler(.failure(error))
                break
            }
        }
    }
    
    func changeSlot(slot: UInt8? = nil, completionHandler: @escaping (Result<Void, Error>) -> Void){
        var command: Data!
        if let slot = slot {
            command = Data([0xFF, slot])
        } else {
            command = Data([0xFF])
        }
        
        peripheral.writeValue(ofCharacWithUUID: PuckPeripheral.commandUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid, value: command, type: .withResponse) { (result) in
            self.peripheral.readValue(ofCharacWithUUID: PuckPeripheral.commandUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid) { (result) in
                switch result {
                case .success(_):
                    completionHandler(.success(()))
                    break
                case .failure(let error):
                    completionHandler(.failure(error))
                    break
                }
            }
        }
    }
    
    func readTag(slot: UInt8 = 255, completionHandler: @escaping (StatusResult<Data, TagStatus, Error>) -> Void){
        _readTag(slot: slot, completionHandler: completionHandler)
    }
    
    fileprivate func _readTag(slot: UInt8, startPage: UInt8 = 0, count: UInt8 = 63, accumulatedData: Data? = nil, completionHandler: @escaping (StatusResult<Data, TagStatus, Error>) -> Void){
        let command = Data([0x02, slot, startPage, count])
        print("Reading from \(peripheral.name ?? "puck") in slot \(slot) at page \(startPage) for \(count) pages")
        completionHandler(.status(TagStatus(slot: slot, start: Int(startPage) * 4, count: Int(count) * 4, total: 572)))
        self.peripheral.writeValue(ofCharacWithUUID: PuckPeripheral.commandUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid, value: command, type: .withResponse) { (result) in
            switch result {
            case .success():
                self.peripheral.readValue(ofCharacWithUUID: PuckPeripheral.responseUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid) { (result) in
                    switch result {
                    case .success(let response):
                        let lastSlot = response[1]
                        let lastStart = response[2]
                        let lastCount = response[3]
                        let nextPage = lastStart + lastCount
                        let accumulatedData = Data((accumulatedData ?? Data(count: 0)) + response[4..<(4 + (Int(lastCount) * 4))])
                        
                        if startPage + count >= 143 {
                            completionHandler(.status(TagStatus(slot: slot, start: 572, count: 0, total: 572)))
                            completionHandler(.success(accumulatedData))
                        } else {
                            self._readTag(slot: lastSlot, startPage: nextPage, count: min(UInt8(143) - startPage - count, count), accumulatedData: accumulatedData, completionHandler: completionHandler)
                        }
                        break
                    case .failure(let error):
                        completionHandler(.failure(error))
                        break
                    }
                }
                break
            case .failure(let error):
                completionHandler(.failure(error))
                break
            }
        }
    }
    
    func readAllTags(completionHandler: @escaping (StatusResult<[Data], TagStatus, Error>) -> Void){
        getSlotSummary { (result) in
            switch result {
            case .success(let tagInformation):
                self._readAllTags(slot: 0, count: tagInformation.total, completionHandler: completionHandler)
                break
            case .failure(let error):
                completionHandler(.failure(error))
                break
            }
        }
    }
    
    fileprivate func _readAllTags(slot: UInt8 = 0, count: UInt8 = 5, tags: [Data] = [], completionHandler: @escaping (StatusResult<[Data], TagStatus, Error>) -> Void){
        if slot < count {
            readTag(slot: slot) { (result) in
                switch result {
                case .status(let status):
                    completionHandler(.status(status))
                case .success(let tag):
                    var tags: [Data] = tags
                    tags.append(Data(tag))
                    self._readAllTags(slot: slot + 1, count: count, tags: tags, completionHandler: completionHandler)
                    break
                case .failure(let error):
                    completionHandler(.failure(error))
                    break
                }
            }
        } else {
            completionHandler(.success(tags))
        }
    }
    
    func writeTag(toSlot slot: UInt8 = 255, using tag: Data, completionHandler: @escaping (StatusResult<Void, TagStatus, Error>) -> Void){
        var data = Data(count: 572)
        data[0..<tag.count] = tag[0..<tag.count]
        _writeTag(toSlot: slot, withData: data, completionHandler: completionHandler)
    }
    
    fileprivate func _writeTag(toSlot slot: UInt8, atPage startPage: UInt8 = 0, withData data: Data, completionHandler: @escaping (StatusResult<Void, TagStatus, Error>) -> Void){
        if startPage < 143 {
            let dataToWrite = Data(data[(Int(startPage) * 4)..<min(((Int(startPage) + 4) * 4), 572)])
            let command = Data([0x03, slot, startPage] + dataToWrite)
            print("Writing to \(self.name) in slot \(slot) at page \(startPage) for \(dataToWrite.count) bytes")
            completionHandler(.status(TagStatus(slot: slot, start: Int(startPage) * 4, count: dataToWrite.count, total: 572)))
            peripheral.writeValue(ofCharacWithUUID: PuckPeripheral.commandUuid, fromServiceWithUUID: PuckPeripheral.serviceUuid, value: command, type: .withResponse) { (result) in
                switch result {
                case .success(_):
                    self._writeTag(toSlot: slot, atPage: startPage + 4, withData: data, completionHandler: completionHandler)
                    break
                case .failure(let error):
                    completionHandler(.failure(error))
                    break
                }
            }
        } else {
            completionHandler(.status(TagStatus(slot: slot, start: 572, count: 0, total: 572)))
            completionHandler(.success(()))
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
