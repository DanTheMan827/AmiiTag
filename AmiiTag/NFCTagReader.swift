//
//  NFCTagReader.swift
//  AmiiTag
//
//  Created by Daniel Radtke on 2/15/21.
//  Copyright © 2021 Daniel Radtke. All rights reserved.
//

import Foundation
import CoreNFC



class NFCTagReader: NSObject, NFCTagReaderSessionDelegate {
    fileprivate static var sharedInstance = NFCTagReader(completionHandler: nil)
    var tagReaderSession: NFCTagReaderSession? = nil
    
    var completionHandler: ((Result<TagDump, Error>) -> Void)? = nil
    func tagReaderSessionDidBecomeActive(_ session: NFCTagReaderSession) {
        NSLog("tagReaderSessionDidBecomeActive")
    }
    
    func tagReaderSession(_ session: NFCTagReaderSession, didInvalidateWithError error: Error) {
        NSLog("NFCTagReaderSession, didInvalidateWithError \(error)")
    }
    
    func tagReaderSession(_ session: NFCTagReaderSession, didDetect tags: [NFCTag]) {
        guard case let NFCTag.miFare(tag) = tags.first! else {
            self.tagReaderSession?.invalidate(errorMessage: "Invalid tag type.")
            return
        }
        session.connect(to: tags.first!) { (error: Error?) in
            if let error = error {
                session.invalidate(errorMessage: error.localizedDescription)
            } else {
                self.handleConnectedTag(tag: tag)
            }
        }
    }
    
    func writeUidSig(data: Data) {
        let uidHex = data[0..<9].map { String(format: "%02hhx", $0) }.joined()
        
        if NTAG215Tag.uidSignatures[uidHex] != nil {
            return
        }
        
        guard NTAG215Tag.validateOriginality(uid: data.prefix(9), signature: data.suffix(32)) else {
            print("Unable to verify tag signature\n  \(uidHex)\n  \(data.suffix(32).map { String(format: "%02hhx", $0) }.joined())")
            return
        }
        
        let fileManager = FileManager.default
        let documentsURL =  fileManager.urls(for: .documentDirectory, in: .userDomainMask).first!
        
        let sigPath = documentsURL.appendingPathComponent("Signatures").appendingPathComponent("Scanned")
        do
        {
            NTAG215Tag.uidSignatures[uidHex] = Data(data.suffix(32))
            try FileManager.default.createDirectory(atPath: sigPath.path, withIntermediateDirectories: true, attributes: nil)
            try data.write(to: sigPath.appendingPathComponent("\(uidHex).bin"))
            print("Validated signature: \(uidHex)")
        }
        catch let error as NSError
        {
            NSLog("Unable to create directory \(error.debugDescription)")
        }
    }
    
    func handleConnectedTag(tag: NFCMiFareTag) {
        NTAG215Tag.initialize(tag: tag) { result in
            switch result {
            case .success(let ntag215Tag):
                self.tagReaderSession?.invalidate()
                DispatchQueue.main.async {
                    self.writeUidSig(data: ntag215Tag.dump.TagUIDSig!)
                    self.completionHandler?(.success(ntag215Tag.dump))
                    self.completionHandler = nil
                }
            case .failure(let error):
                self.tagReaderSession?.invalidate(errorMessage: error.localizedDescription)
            }
        }
    }
    
    fileprivate func ReadAmiibo(completionHandler: ((Result<TagDump, Error>) -> Void)?){
        self.completionHandler = completionHandler
        self.tagReaderSession = NFCTagReaderSession(pollingOption: NFCTagReaderSession.PollingOption.iso14443, delegate: self)
        
        self.tagReaderSession?.alertMessage = "Hold tag to back of phone!"
        self.tagReaderSession?.begin()
    }
    
    static func ReadAmiibo(completionHandler: ((Result<TagDump, Error>) -> Void)?){
        NFCTagReader.sharedInstance.ReadAmiibo(completionHandler: completionHandler)
    }
    
    init(completionHandler: ((Result<TagDump, Error>) -> Void)?){
        super.init()
        self.completionHandler = completionHandler
    }
}
